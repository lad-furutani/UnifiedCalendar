# 3. ソリューション / プロジェクト構成

過剰分割を避け、製品コード5プロジェクトとテスト1プロジェクトにする。インストーラ、文書、ビルド設定はソリューション直下へ置く。

```text
UnifiedCalendar.sln
├─ src/UnifiedCalendar.App
├─ src/UnifiedCalendar.Core
├─ src/UnifiedCalendar.Infrastructure
├─ src/UnifiedCalendar.Providers.Google
├─ src/UnifiedCalendar.Providers.Microsoft
├─ tests/UnifiedCalendar.Tests
├─ installer/UnifiedCalendar.iss
├─ Directory.Build.props
├─ Directory.Packages.props
└─ docs/
```

| プロジェクト | 主責務 | 参照先 |
| --- | --- | --- |
| App | WPF Views/ViewModels、App起動、Dispatcher、トレイ、ウィンドウ、テーマ/DPI連携 | Core, Infrastructure, Providers.* |
| Core | 共通モデル、契約、同期・Presentation・色・差分の業務ロジック | BCLのみ |
| Infrastructure | JSON Store、DPAPI TokenStore、OS/URI/自動起動/単一インスタンス、Serilog構成 | Core |
| Providers.Google | Google認証、カレンダー列挙、イベント取得、共通モデル変換 | Core, Google SDK |
| Providers.Microsoft | MSAL認証、Graph列挙・取得、共通モデル変換 | Core, Graph SDK, MSAL |
| Tests | Core単体、Store統合、Provider契約、ViewModel、受入基準対応テスト | 全製品プロジェクト |

## 3.1 パッケージ管理

NuGetはDirectory.Packages.propsのCentral Package Managementで固定する。CommunityToolkit.Mvvm、Microsoft.Extensions.Hosting/Http、Serilog.Extensions.Hosting、Serilog.Sinks.File、Serilog.Formatting.Compact、Google.Apis.Calendar.v3、Google.Apis.Auth、Microsoft.Graph、Microsoft.Identity.Client、System.Security.Cryptography.ProtectedData、xUnit、NSubstituteを使用する。Phase 0で.NET 8 LTS互換の安定版を復元テストし、以後は明示変更なしに更新しない。

> 技術設計の補足（移行方針）  将来 .NET 10 以降へ移行しやすいよう、不要な .NET 8 固有API・実装への依存を避ける。TargetFramework等の共通ビルド設定はDirectory.Build.props、NuGetパッケージ版はDirectory.Packages.propsで一元管理する。

## 3.2 ビルド / 発行

- TargetFrameworkはnet8.0-windows。Debug/ReleaseともAnyCPUではなくx64を既定とし、RuntimeIdentifierはwin-x64。

- Releaseはself-contained。PublishSingleFile=false、PublishTrimmed=falseとし、WPF・SDKの動的参照による欠落を避ける。

- ビルド番号やGit SHAはInformationalVersionへ付加できるが、画面表示版はSemVerを正とする。
