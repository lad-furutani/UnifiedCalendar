# 2. 確定技術スタックとアーキテクチャ原則

| 領域 | 採用 |
| --- | --- |
| 開発 / OS | Visual Studio 2022 / Windows 11 x64のみ |
| 言語 / UI / Runtime | C# / WPF / .NET 8 LTS（net8.0-windows） |
| 設計 / MVVM | MVVM / CommunityToolkit.Mvvm |
| Host / DI | Microsoft.Extensions.Hosting Generic Host / Microsoft.Extensions.DependencyInjection |
| 通信 / JSON / 非同期 | 公式SDK、補完時HttpClient、System.Text.Json、async/await + CancellationToken |
| ログ | Serilog（構造化・日次ローテーション・30日保持） |
| テスト | xUnit / NSubstitute / TimeProvider注入 |
| 配布 | Inno Setup、win-x64 self-contained、非single-file、trimming無効 |
| 版管理 | Semantic Versioning MAJOR.MINOR.PATCH、初期正式版1.0.0 |

## 2.1 原則

- CoreはWPF、Google SDK、Microsoft Graph SDK、ファイルシステム、DPAPIへ依存しない。

- 非同期処理は末端までCancellationTokenを渡し、同期APIをUIスレッドで待たない。

- 現在時刻はTimeProviderから取得する。DateTime.Now / DateTimeOffset.Nowの直接使用をコードレビューで禁止する。

- Providerは1カレンダー分の全ページ取得が完了してから結果を返す。途中ページは共有状態へ反映しない。

- 外部境界の失敗は型付き結果へ正規化し、例外文字列をViewModelやログへそのまま流さない。

- 抽象化は差替え価値のある境界だけに置き、各クラスに機械的なインターフェースを作らない。

## 2.2 実行時データフロー

```text
WPF Views
   │ Binding / Command
ViewModels ── IUiDispatcher ── WPF Dispatcher
   │
CalendarPresentationService ── SnapshotDiffer ── TimelineItemViewModel
   ▲
CalendarSyncService ── ImmutableSyncSnapshot ── ICacheStore
   │                         │
   ├─ ICalendarProvider.Google ── Google Calendar SDK
   └─ ICalendarProvider.Microsoft ── Graph SDK + MSAL
                             │
ISettingsStore / ITokenStore / OS services / Serilog
```
