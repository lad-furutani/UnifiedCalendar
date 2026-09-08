# UnifiedCalendar

Google Calendar と Microsoft 365 の予定を、Windows 上の1つの時系列へまとめて表示する参照専用デスクトップアプリです。複数アカウント・複数カレンダーに対応し、タスクトレイに常駐して予定を定期的に更新します。

現在のリリース: **1.0.0**

## 主な機能

- Google Calendar / Microsoft 365 の複数アカウントを統合表示
- 終日予定と時刻指定予定を日付ごとのタイムラインへ整理
- 表示対象カレンダー、表示日数、文字サイズ、表示密度、色分けルールを設定可能
- 予定の詳細、元の予定、オンライン会議へのリンクを表示
- 手動更新、定期更新、スリープ復帰時の更新に対応
- オフライン時はローカルキャッシュから直近の予定を表示
- Windows のライト / ダークテーマ、DPI、ローカルタイムゾーンの変更に追従
- タスクトレイ常駐、Windows ログオン時の自動起動、単一インスタンス動作

UnifiedCalendar は閲覧に特化しています。アプリ内での予定の作成・編集・削除、検索・絞り込み、通知、印刷・エクスポート、自動アップデート、Push / Webhook 更新は提供しません。

## 動作環境

- Windows 11 x64
- 日本語 UI
- 配布版は .NET 8 の自己完結型アプリケーションであり、.NET ランタイムの別途インストールは不要です

## 開発環境

- Windows 11 x64
- .NET 8 SDK
- Visual Studio 2022（任意）
- PowerShell
- Inno Setup 6（インストーラを作成する場合）

リポジトリのルートで次を実行すると、依存関係の復元、Release ビルド、テスト、自己完結型発行を順に行います。

```powershell
.\build.ps1
```

発行物は `artifacts\publish\win-x64` に生成されます。資格情報を設定せずにビルドすることもできますが、その発行物では該当プロバイダーのアカウント追加が無効になり、ビルド終了時に警告が表示されます。

テストのみを実行する場合は次のコマンドを使用します。

```powershell
dotnet test .\UnifiedCalendar.sln --configuration Release
```

## OAuth クライアント資格情報

カレンダーサービスへ接続するには、Google Cloud と Microsoft Entra ID でデスクトップアプリ用の OAuth クライアントを登録し、次の環境変数を設定します。

| 環境変数 | 用途 |
| --- | --- |
| `UnifiedCalendar__Google__ClientId` | Google OAuth クライアント ID |
| `UnifiedCalendar__Google__ClientSecret` | Google デスクトップクライアントのクライアントシークレット |
| `UnifiedCalendar__Microsoft__ClientId` | Microsoft Entra ID のアプリケーション（クライアント）ID |

Google の Client ID と Client Secret は必ず同じクライアントの組として設定してください。Microsoft はパブリックデスクトップクライアントとして動作するため、Client Secret は使用しません。

この公開リポジトリに配布バイナリ用の資格情報は含まれていません。フォークして自分でビルドする場合は、自分の Google / Microsoft OAuth クライアントを用意してください。

PowerShell セッションで設定してビルドする例:

```powershell
$env:UnifiedCalendar__Google__ClientId = "your-google-client-id"
$env:UnifiedCalendar__Google__ClientSecret = "your-google-client-secret"
$env:UnifiedCalendar__Microsoft__ClientId = "your-microsoft-client-id"

.\build.ps1
```

`build.ps1` は値を発行アセンブリへ埋め込みます。環境変数の値や資格情報をソースコード、設定ファイル、コミットへ含めないでください。実行時に同名の環境変数を設定すると、埋め込み値より優先して使用されます。

登録方式、必要な権限、Google のテストユーザー運用などの詳細は [OAuth認証・トークン保存設計](docs/technical-design/08-oauth-and-token-storage.md) を参照してください。

## 初回のアカウント追加について

Google アカウントを追加すると、次の警告画面が表示されます。**これは想定どおりの表示です。**

> このアプリは Google で確認されていません

UnifiedCalendar は社内配布のため Google の審査を受けていません。そのため、アカウントを追加するたびにこの画面が表示されます。

### 通過手順

1. 画面**左下の「詳細」**をクリックします。

   **右側の青いボタン「安全なページに戻る」を押すと中断されます。押さないでください。**

2. 展開された下部の **「UnifiedCalendar（安全ではないページ）に移動」** をクリックします。

3. アクセス権の確認画面で **「次へ」** をクリックし、続けて許可します。

### 求められる権限

**「Google カレンダーを使用してアクセスできるすべてのカレンダーの参照、ダウンロード」のみ**です。予定の作成・変更・削除は行いません。

詳細は[プライバシーポリシー](https://lad-furutani.github.io/UnifiedCalendar/privacy)をご覧ください。

## インストーラの作成

先に `build.ps1` で発行物を作成し、Inno Setup 6 をインストールしてから実行します。

```powershell
.\build-installer.ps1
```

配布用ビルドでは、発行物に Google / Microsoft の資格情報がすべて埋め込まれていることと、生成物のコード署名を既定で検証します。署名には、ビルドマシンの Inno Setup に `MySignTool` の登録が必要です。

ローカル確認用に、資格情報と署名を意図的に省略したインストーラを作成する場合:

```powershell
.\build-installer.ps1 -NoSign -AllowMissingCredentials
```

生成先は `artifacts\installer\UnifiedCalendar-Setup-1.0.0.exe` です。インストーラはユーザー単位でインストールされ、管理者権限を必要としません。

## データとプライバシー

ユーザーデータは `%LOCALAPPDATA%\UnifiedCalendar` 以下に保存されます。

```text
%LOCALAPPDATA%\UnifiedCalendar\
├─ settings\settings.json
├─ tokens\google\...
├─ tokens\microsoft\...
├─ cache\accounts\...
├─ logs\unifiedcalendar-YYYYMMDD.log
└─ recovery\
```

- OAuth トークンは Windows DPAPI CurrentUser で暗号化され、同じ Windows ユーザーとPCでのみ復号できます
- 設定と予定キャッシュは平文 JSON です。予定の件名、説明、場所、URL、アカウント表示情報がローカルに残る点に注意してください
- ログにはトークン、メールアドレス、表示名、予定内容、URL、生のカレンダー ID / イベント ID を記録しません
- ログは日単位でローテーションし、30日分を保持します
- アンインストールしてもユーザーデータは削除されません

## ソリューション構成

| プロジェクト | 役割 |
| --- | --- |
| `UnifiedCalendar.App` | WPF UI、MVVM、アプリケーション起動とDI構成 |
| `UnifiedCalendar.Core` | ドメインモデル、同期・表示ロジック、抽象インターフェース |
| `UnifiedCalendar.Infrastructure` | JSON保存、DPAPI、ログ、OS連携 |
| `UnifiedCalendar.Providers.Google` | Google Calendar API と OAuth 連携 |
| `UnifiedCalendar.Providers.Microsoft` | Microsoft Graph と MSAL 連携 |
| `UnifiedCalendar.Tests` | xUnit によるユニット・統合・WPFテスト |

## ライセンス

本プロジェクトは Apache License 2.0 のもとで提供されます。詳細は [LICENSE](LICENSE) を参照してください。

同梱している第三者コンポーネントの表示は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) にあります。

## ドキュメント

- [製品仕様書](docs/specification.md)
- [技術設計書](docs/technical-design/README.md)
- [テスト戦略と受入マトリクス](docs/technical-design/15-testing-and-acceptance.md)
- [1.0.0 実機受入結果](docs/acceptance-phase8.md)
- [セキュリティ・プライバシー設計](docs/technical-design/16-security-and-privacy.md)

実装や仕様を変更する場合は、コードとあわせて関連ドキュメントも更新してください。
