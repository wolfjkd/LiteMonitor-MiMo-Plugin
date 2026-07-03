# LiteMonitor MiMo Plugin

小米 MiMo Token Plan 套餐用量监控插件，适用于 [LiteMonitor](https://github.com/wolfjkd/LiteMonitor) 项目。

## 功能特性

- 实时显示小米 MiMo Token Plan 套餐用量
- 自动从 Edge/Chrome 浏览器获取 Cookie（无需手动输入）
- 支持手动输入 Cookie 作为备选方案
- 数据单位自动转换为 "亿"
- 百分比显示修正为正确数值

## 显示效果

```
套餐额度: 15.37亿 / 110亿 (14%)
补偿额度: 0亿 / 0亿 (0%)
```

## 安装方法

### 方法一：自动安装（推荐）

1. 将 `MiMoToken.json` 复制到 LiteMonitor 的 `resources/plugins/` 目录
2. 将 `MiMoNative.cs` 复制到 LiteMonitor 的 `src/Plugins/Native/` 目录
3. 在 `PluginExecutor.cs` 中添加 `native://mimo` 协议处理
4. 重新编译 LiteMonitor

### 方法二：手动输入 Cookie

1. 在浏览器中登录 [platform.xiaomimimo.com](https://platform.xiaomimimo.com)
2. 按 `F12` 打开开发者工具 → `Application` → `Cookies`
3. 复制所有 Cookie
4. 在插件设置中粘贴 Cookie

## 配置说明

### 插件配置 (MiMoToken.json)

```json
{
    "id": "MiMoToken",
    "meta": {
        "name": "小米MiMo套餐",
        "version": "1.0.0",
        "author": "wolfjkd",
        "description": "显示小米MiMo Token Plan套餐用量"
    },
    "inputs": [
        {
            "key": "cookies",
            "label": "Cookie",
            "type": "text",
            "default": "",
            "placeholder": "留空则自动从浏览器获取"
        }
    ],
    "execution": {
        "type": "chain",
        "interval": 60,
        "steps": [
            {
                "id": "fetch_mimo",
                "url": "native://mimo?cookies={{cookies}}",
                "method": "GET",
                "response_format": "json",
                "extract": {
                    "plan_used": "plan_used",
                    "plan_limit": "plan_limit",
                    "plan_percent": "plan_percent",
                    "comp_used": "comp_used",
                    "comp_limit": "comp_limit",
                    "comp_percent": "comp_percent"
                }
            }
        ]
    },
    "outputs": [
        {
            "key": "plan",
            "label": "套餐额度",
            "format_val": "{{plan_used}}亿 / {{plan_limit}}亿 ({{plan_percent}}%)"
        },
        {
            "key": "comp",
            "label": "补偿额度",
            "format_val": "{{comp_used}}亿 / {{comp_limit}}亿 ({{comp_percent}}%)"
        }
    ]
}
```

## Cookie 获取机制

### 注意：自动获取功能有限制

由于新版 Edge/Chrome 浏览器启用了 **App Bound Encryption**（应用绑定加密），代码中实现的自动获取逻辑**目前无法正常工作**。浏览器会使用专用密钥加密Cookie，外部程序无法解密。

### 当前可用方式

1. **手动输入**（推荐）：在浏览器中登录后，通过开发者工具复制Cookie，粘贴到插件设置中
2. **持久化存储**：输入的 Cookie 会自动保存到 `%LOCALAPPDATA%\LiteMonitor\mimo_cookies.txt`，下次启动时自动加载

### Cookie 获取步骤

1. 在浏览器中登录 [platform.xiaomimimo.com](https://platform.xiaomimimo.com)
2. 按 `F12` 打开开发者工具
3. 切换到 `Application` → `Cookies` → `https://platform.xiaomimimo.com`
4. 右键点击任意Cookie → `Copy all`
5. 将复制的Cookie粘贴到插件设置中

## 依赖

- .NET 8.0
- SQLite (用于读取浏览器 Cookie 数据库)
- Microsoft.Data.Sqlite NuGet 包

## 开源协议

MIT License

## 相关项目

- [LiteMonitor](https://github.com/wolfjkd/LiteMonitor) - 桌面监控工具