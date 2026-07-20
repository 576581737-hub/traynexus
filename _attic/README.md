# _attic/ — 归档目录

本目录存放**当前不参与编译、不被程序使用**的历史文件。保留只为可追溯，可随时整体删除。

## 归档内容

| 子目录 | 内容 | 归档原因 |
|---|---|---|
| `src/` | `WebViewPanel.cs` / `BridgeApi.cs` | 项目已从 WebView2 + HTML 全面切换到纯 WinForms + GDI+ 自绘，这两个文件不再被 `build.bat / build.rsp` 编译，也无任何调用 |
| `lib/` | `Microsoft.Web.WebView2.Core.dll` / `Microsoft.Web.WebView2.WinForms.dll` / `WebView2Loader.dll` | WebView2 依赖已废弃，单 exe 分发不需要 |
| `logo/` | `logo_1024.png` / `logo_2048.png` | 超大 PNG 只在合成 `resources/tray_default.ico` 时用一次（LANCZOS 重采样），后续维护改用 `logo.svg`（保留在 `svg/`），不需要频繁访问 |
| `preview/` | `preview_on_black/blue/gradient/white.png` | 品牌视觉预览稿，仅供设计参考，不参与编译 |
| `svg/` | `logo.svg` / `github-icon.svg` / `功能图标墙 .svg` / `功能图标墙-彩色.svg` | SVG 源已被吸收为 GDI+ 手写路径（`DrawIcon`），设计定稿后不再作为运行时资源 |
| `archive/prototypes/` | `traynexus-ui-prototype.html` / `ultracode-*.html` | 早期 HTML/CSS 视觉原型，UI 已完全 WinForms 化，仅保留视觉参考 |

## 恢复方式
如需恢复任一文件，直接从此处 `mv` 回原位置即可。`build.bat` / `build.rsp` 未引用这些文件，不会破坏构建。
