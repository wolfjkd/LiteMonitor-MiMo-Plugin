using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace LiteMonitor.src.Plugins.Native
{
    public static class MiMoNative
    {
        private static readonly string BASE_URL = "https://platform.xiaomimimo.com";
        private static readonly string[] MIMO_DOMAINS = { "xiaomimimo.com", "xiaomi.com", "mi.com" };
        private static readonly HashSet<string> REQUIRED_COOKIES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serviceToken", "api-platform_serviceToken",
            "api-platform_ph", "api-platform_slh",
            "userId", "xiaomichatbot_ph"
        };

        private static HttpClient _client = CreateClient();
        private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteMonitor", "mimo_plugin.log");

        private static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using var writer = File.AppendText(_logPath);
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
            catch { }
        }

        private static HttpClient CreateClient()
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 8,
                UseProxy = true,
                Proxy = System.Net.WebRequest.GetSystemWebProxy(),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                }
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("x-timezone", "Asia/Shanghai");
            client.DefaultRequestHeaders.Add("origin", BASE_URL);
            client.DefaultRequestHeaders.Add("Referer", $"{BASE_URL}/console/plan-manage");
            return client;
        }

        private static Dictionary<string, string> _headers(string cookies)
        {
            return new Dictionary<string, string>
            {
                { "Cookie", cookies },
                { "Content-Type", "application/json" },
                { "Accept", "*/*" }
            };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr szDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            uint dwFlags,
            ref DATA_BLOB pDataOut);

        private static byte[] DecryptDpapi(byte[] encrypted)
        {
            if (encrypted == null || encrypted.Length == 0)
                return null;

            DATA_BLOB blobIn = new DATA_BLOB { cbData = (uint)encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
            Marshal.Copy(encrypted, 0, blobIn.pbData, encrypted.Length);
            DATA_BLOB blobOut = new DATA_BLOB();
            DATA_BLOB optionalEntropy = new DATA_BLOB();

            try
            {
                if (CryptUnprotectData(ref blobIn, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0, ref blobOut))
                {
                    byte[] result = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, result, 0, (int)blobOut.cbData);
                    return result;
                }
                return null;
            }
            finally
            {
                if (blobIn.pbData != IntPtr.Zero)
                    Marshal.FreeHGlobal(blobIn.pbData);
                if (blobOut.pbData != IntPtr.Zero)
                    Marshal.FreeHGlobal(blobOut.pbData);
            }
        }

        private static byte[] GetEncryptionKey(string userDataDir)
        {
            try
            {
                string localStatePath = Path.Combine(userDataDir, "Local State");
                if (!File.Exists(localStatePath))
                    return null;

                string content = File.ReadAllText(localStatePath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(content);
                
                var osCrypt = doc.RootElement.GetProperty("os_crypt");
                
                string encryptedKey = null;
                if (osCrypt.TryGetProperty("encrypted_key", out var legacyKey))
                {
                    encryptedKey = legacyKey.GetString();
                    Log("使用 encrypted_key (传统)");
                }
                
                if (string.IsNullOrEmpty(encryptedKey))
                    return null;

                byte[] key = Convert.FromBase64String(encryptedKey);
                
                if (key.Length > 5 && key[0] == 'D' && key[1] == 'P' && key[2] == 'A' && key[3] == 'P' && key[4] == 'I')
                {
                    byte[] result = DecryptDpapi(key.Skip(5).ToArray());
                    Log($"DPAPI 解密密钥成功，长度: {result?.Length ?? 0}");
                    return result;
                }
                
                Log($"密钥无前缀 DPAPI，长度: {key.Length}");
                return key;
            }
            catch (Exception ex)
            {
                Log($"获取加密密钥异常: {ex.Message}");
                return null;
            }
        }

        private static string DecryptCookieValue(byte[] encrypted, byte[] aesKey = null, string host = "")
        {
            if (encrypted == null || encrypted.Length == 0)
                return "";

            try
            {
                if (encrypted.Length > 3 && encrypted[0] == 'v' && encrypted[1] == '1' && encrypted[2] == '0')
                {
                    Log($"AES-GCM v10 解密: length={encrypted.Length}, host={host}, aesKey={aesKey?.Length ?? 0}");
                    if (aesKey == null)
                        return "";

                    byte[] raw = encrypted.Skip(3).ToArray();
                    Log($"AES-GCM raw length={raw.Length}");
                    
                    if (raw.Length < 28)
                    {
                        Log($"AES-GCM raw长度不足: {raw.Length}");
                        return "";
                    }

                    byte[] nonce = raw.Take(12).ToArray();
                    byte[] tag = raw.Skip(raw.Length - 16).Take(16).ToArray();
                    byte[] ciphertext = raw.Skip(12).Take(raw.Length - 28).ToArray();

                    Log($"AES-GCM nonce={BitConverter.ToString(nonce)}, tag={BitConverter.ToString(tag)}, ciphertext={ciphertext.Length}bytes");

                    using var aesGcm = new AesGcm(aesKey);
                    byte[] plaintext = new byte[ciphertext.Length];
                    
                    try
                    {
                        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, null);
                        string result = Encoding.UTF8.GetString(plaintext);
                        Log($"AES-GCM v10 不使用AAD解密成功: {result.Substring(0, Math.Min(50, result.Length))}...");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Log($"AES-GCM v10 不使用AAD解密失败: {ex.Message}");
                        try
                        {
                            byte[] aad = !string.IsNullOrEmpty(host) ? Encoding.UTF8.GetBytes(host) : null;
                            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                            string result = Encoding.UTF8.GetString(plaintext);
                            Log($"AES-GCM v10 使用AAD解密成功: {result.Substring(0, Math.Min(50, result.Length))}...");
                            return result;
                        }
                        catch (Exception ex2)
                        {
                            Log($"AES-GCM v10 使用AAD解密也失败: {ex2.Message}");
                            return "";
                        }
                    }
                }
                else if (encrypted.Length > 3 && encrypted[0] == 'v' && encrypted[1] == '2' && encrypted[2] == '0')
                {
                    Log($"AES-GCM v20 解密: length={encrypted.Length}, host={host}, aesKey={aesKey?.Length ?? 0}");
                    if (aesKey == null)
                        return "";

                    byte[] raw = encrypted.Skip(3).ToArray();
                    if (raw.Length < 28)
                    {
                        Log($"AES-GCM v20 raw长度不足: {raw.Length}");
                        return "";
                    }

                    byte[] nonce = raw.Take(12).ToArray();
                    byte[] tag = raw.Skip(raw.Length - 16).Take(16).ToArray();
                    byte[] ciphertext = raw.Skip(12).Take(raw.Length - 28).ToArray();

                    using var aesGcm = new AesGcm(aesKey);
                    byte[] plaintext = new byte[ciphertext.Length];
                    
                    try
                    {
                        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, null);
                        string result = Encoding.UTF8.GetString(plaintext);
                        Log($"AES-GCM v20 不使用AAD解密成功: {result.Substring(0, Math.Min(50, result.Length))}...");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Log($"AES-GCM v20 不使用AAD解密失败: {ex.Message}");
                        try
                        {
                            byte[] aad = !string.IsNullOrEmpty(host) ? Encoding.UTF8.GetBytes(host) : null;
                            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                            string result = Encoding.UTF8.GetString(plaintext);
                            Log($"AES-GCM v20 使用AAD解密成功: {result.Substring(0, Math.Min(50, result.Length))}...");
                            return result;
                        }
                        catch (Exception ex2)
                        {
                            Log($"AES-GCM v20 使用AAD解密也失败: {ex2.Message}");
                            return "";
                        }
                    }
                }

                byte[] decrypted = DecryptDpapi(encrypted);
                if (decrypted != null)
                {
                    string result = Encoding.UTF8.GetString(decrypted);
                    Log($"DPAPI 解密成功: {result.Substring(0, Math.Min(50, result.Length))}...");
                    return result;
                }
                return "";
            }
            catch (Exception ex)
            {
                Log($"DecryptCookieValue 异常: {ex.Message}");
                return "";
            }
        }

        private static string FindCookieDb(string userData)
        {
            string[] candidates = {
                Path.Combine(userData, "Default", "Cookies"),
                Path.Combine(userData, "Default", "Network", "Cookies")
            };
            foreach (string p in candidates)
                if (File.Exists(p))
                    return p;

            if (Directory.Exists(userData))
            {
                foreach (string item in Directory.GetDirectories(userData))
                {
                    foreach (string sub in new[] { "", "Network" })
                    {
                        string candidate = Path.Combine(item, sub, "Cookies");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            return "";
        }

        private static string ReadBrowserCookies(string browserName, string userDataDir)
        {
            string cookieDb = FindCookieDb(userDataDir);
            Log($"{browserName} Cookie数据库路径: {cookieDb}, 存在: {File.Exists(cookieDb)}");
            if (!File.Exists(cookieDb))
                return "";

            byte[] aesKey = null;
            try
            {
                aesKey = GetEncryptionKey(userDataDir);
                Log($"{browserName} AES密钥获取: {(aesKey != null ? $"成功 ({aesKey.Length}字节)" : "失败")}");
            }
            catch (Exception ex)
            {
                Log($"{browserName} 获取AES密钥异常: {ex.Message}");
            }

            string tempDb = "";
            int retryCount = 0;
            const int maxRetries = 3;
            const int retryDelayMs = 1000;

            while (retryCount < maxRetries)
            {
                try
                {
                    tempDb = Path.Combine(Path.GetTempPath(), $"mimo_cookies_{Guid.NewGuid()}.db");
                    Log($"{browserName} 尝试复制数据库到临时文件: {tempDb} (重试 {retryCount + 1}/{maxRetries})");
                    File.Copy(cookieDb, tempDb, true);
                    Log($"{browserName} 数据库复制成功");
                    break;
                }
                catch (IOException ex) when (ex.Message.Contains("being used") || ex.Message.Contains("正在使用") || ex.Message.Contains("锁定"))
                {
                    retryCount++;
                    Log($"{browserName} 数据库被锁定，等待 {retryDelayMs}ms 后重试 ({retryCount}/{maxRetries}): {ex.Message}");
                    System.Threading.Thread.Sleep(retryDelayMs);
                    if (retryCount >= maxRetries)
                    {
                        Log($"{browserName} 数据库复制失败，尝试直接读取原始数据库");
                        tempDb = cookieDb;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"{browserName} 数据库复制异常: {ex.Message}");
                    return "";
                }
            }

            bool isOriginalDb = tempDb == cookieDb;

            try
            {
                string connectionString = isOriginalDb 
                    ? $"Data Source={tempDb};Version=3;ReadOnly=True;Mode=ReadOnly;Cache=Shared" 
                    : $"Data Source={tempDb};Version=3;ReadOnly=True";
                
                Log($"{browserName} 打开数据库连接: {connectionString}");
                using var conn = new SQLiteConnection(connectionString);
                conn.Open();
                Log($"{browserName} 数据库连接成功");

                string conditions = string.Join(" OR ", MIMO_DOMAINS.Select(d => $"host_key LIKE '%{d}%'"));
                string sql = $"SELECT host_key, name, encrypted_value FROM cookies WHERE {conditions}";

                using var cmd = new SQLiteCommand(sql, conn);
                using var reader = cmd.ExecuteReader();

                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> parts = new List<string>();

                int totalRows = 0;
                List<string> foundNames = new List<string>();
                while (reader.Read())
                {
                    totalRows++;
                    string name = reader.GetString(1);
                    string host = reader.GetString(0);
                    foundNames.Add(name);
                    
                    if (!REQUIRED_COOKIES.Contains(name) || seen.Contains(name))
                        continue;

                    byte[] encVal = (byte[])reader.GetValue(2);
                    string value = DecryptCookieValue(encVal, aesKey, host);
                    
                    if (string.IsNullOrEmpty(value))
                    {
                        Log($"{browserName} Cookie {name} 解密失败");
                        continue;
                    }

                    value = value.Trim('"');
                    seen.Add(name);

                    if (name.IndexOf("servicetoken", StringComparison.OrdinalIgnoreCase) >= 0)
                        parts.Add($"{name}=\"{value}\"");
                    else
                        parts.Add($"{name}={value}");
                }
                
                Log($"{browserName} 查询到{totalRows}条Cookie记录: {string.Join(", ", foundNames)}");
                Log($"{browserName} 需要的Cookie: {string.Join(", ", REQUIRED_COOKIES)}");
                Log($"{browserName} 成功提取{parts.Count}个需要的Cookie");
                return string.Join("; ", parts);
            }
            catch (Exception ex)
            {
                Log($"{browserName} 读取Cookie异常: {ex.Message}");
                return "";
            }
            finally
            {
                try { if (!isOriginalDb && !string.IsNullOrEmpty(tempDb) && File.Exists(tempDb)) File.Delete(tempDb); } catch { }
            }
        }

        private static string GetCookiesFromBrowser()
        {
            Dictionary<string, string> browsers = new Dictionary<string, string>
            {
                { "Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data") },
                { "Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data") }
            };

            Log("开始从浏览器获取Cookie");
            foreach (var kv in browsers)
            {
                Log($"检查浏览器: {kv.Key}, 路径: {kv.Value}");
                if (!Directory.Exists(kv.Value))
                {
                    Log($"{kv.Key} 用户数据目录不存在");
                    continue;
                }

                string cookies = ReadBrowserCookies(kv.Key, kv.Value);
                if (!string.IsNullOrEmpty(cookies))
                {
                    Log($"从 {kv.Key} 获取到Cookie成功");
                    return cookies;
                }
                else
                {
                    Log($"从 {kv.Key} 获取Cookie为空");
                }
            }
            Log("所有浏览器均未获取到有效的MiMo Cookie，尝试读取保存的Cookie");
            return GetSavedCookies();
        }

        private static string GetSavedCookies()
        {
            try
            {
                string[] possiblePaths = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteMonitor", "mimo_cookies.txt"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteMonitor", "mimo_cookies.txt"),
                    Path.Combine(AppContext.BaseDirectory, "mimo_cookies.txt")
                };
                
                foreach (string cookieFile in possiblePaths)
                {
                    if (File.Exists(cookieFile))
                    {
                        string cookies = File.ReadAllText(cookieFile, Encoding.UTF8).Trim();
                        if (!string.IsNullOrEmpty(cookies))
                        {
                            Log($"从配置文件读取到Cookie: {cookieFile}");
                            return cookies;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"读取保存的Cookie失败: {ex.Message}");
            }
            return "";
        }

        private static void SaveCookies(string cookies)
        {
            string[] possiblePaths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteMonitor", "mimo_cookies.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteMonitor", "mimo_cookies.txt"),
                Path.Combine(AppContext.BaseDirectory, "mimo_cookies.txt")
            };
            
            foreach (string cookieFile in possiblePaths)
            {
                try
                {
                    string dir = Path.GetDirectoryName(cookieFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(cookieFile, cookies, Encoding.UTF8);
                    Log($"Cookie已保存到配置文件: {cookieFile}");
                    return;
                }
                catch (Exception ex)
                {
                    Log($"保存Cookie到 {cookieFile} 失败: {ex.Message}");
                }
            }
        }

        public static async Task<string> FetchUsageAsync()
        {
            try
            {
                string cookies = GetCookiesFromBrowser();
                Log($"获取到Cookie: {(string.IsNullOrEmpty(cookies) ? "空" : $"包含{System.Text.RegularExpressions.Regex.Matches(cookies, "; ").Count + 1}个键值对")}");
                if (string.IsNullOrEmpty(cookies))
                    throw new Exception("未找到有效的MiMo Cookie，请先在浏览器中登录 platform.xiaomimimo.com");

                return await FetchUsageWithCookiesAsync(cookies);
            }
            catch (Exception ex)
            {
                Log($"FetchUsageAsync 失败: {ex.Message}");
                throw;
            }
        }

        public static async Task<string> FetchUsageWithCookiesAsync(string cookies)
        {
            try
            {
                Log($"开始调用API: {BASE_URL}/api/v1/tokenPlan/usage");
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BASE_URL}/api/v1/tokenPlan/usage");
                foreach (var h in _headers(cookies))
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);

                using var response = await _client.SendAsync(request);
                Log($"API响应状态: {response.StatusCode}");
                
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                Log($"API响应内容长度: {json.Length}");
                
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("code", out var codeElement) && codeElement.GetInt32() != 0)
                {
                    string msg = doc.RootElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "未知错误";
                    Log($"API返回错误: code={codeElement.GetInt32()}, msg={msg}");
                    throw new Exception(msg);
                }

                if (!doc.RootElement.TryGetProperty("data", out var data))
                {
                    Log($"API响应无data字段: {json.Substring(0, Math.Min(200, json.Length))}");
                    throw new Exception("API响应数据格式异常");
                }
                
                var usageItems = data.GetProperty("usage").GetProperty("items");

                var plan = new Dictionary<string, object>();
                var comp = new Dictionary<string, object>();

                foreach (var item in usageItems.EnumerateArray())
                {
                    string name = item.GetProperty("name").GetString();
                    Log($"发现usage项: {name}");
                    if (name == "plan_total_token")
                    {
                        plan["used"] = Math.Round(item.GetProperty("used").GetInt64() / 100000000.0, 2);
                        plan["limit"] = Math.Round(item.GetProperty("limit").GetInt64() / 100000000.0, 2);
                        plan["percent"] = Math.Round(item.GetProperty("percent").GetDouble() * 100, 2);
                    }
                    else if (name == "compensation_total_token")
                    {
                        comp["used"] = Math.Round(item.GetProperty("used").GetInt64() / 100000000.0, 2);
                        comp["limit"] = Math.Round(item.GetProperty("limit").GetInt64() / 100000000.0, 2);
                        comp["percent"] = Math.Round(item.GetProperty("percent").GetDouble() * 100, 2);
                    }
                }

                var result = new
                {
                    plan_used = plan.ContainsKey("used") ? plan["used"] : 0,
                    plan_limit = plan.ContainsKey("limit") ? plan["limit"] : 0,
                    plan_percent = plan.ContainsKey("percent") ? plan["percent"] : 0,
                    comp_used = comp.ContainsKey("used") ? comp["used"] : 0,
                    comp_limit = comp.ContainsKey("limit") ? comp["limit"] : 0,
                    comp_percent = comp.ContainsKey("percent") ? comp["percent"] : 0,
                    plan_name = "套餐额度",
                    comp_name = "补偿额度"
                };

                SaveCookies(cookies);
                string jsonResult = JsonSerializer.Serialize(result);
                Log($"返回结果: plan_used={result.plan_used}, plan_limit={result.plan_limit}, plan_percent={result.plan_percent}");
                Log($"JSON输出: {jsonResult}");
                return jsonResult;
            }
            catch (Exception ex)
            {
                Log($"FetchUsageWithCookiesAsync 失败: {ex.Message}");
                throw;
            }
        }
    }
}