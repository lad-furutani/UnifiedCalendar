# 8. OAuth認証・トークン保存設計

## 8.1 認証フロー

1.  設定または未登録状態の追加ボタンからだけ認証を開始する。認証エラー時も自動でブラウザを開かない。

2.  公式認証ライブラリでPKCE/stateを用い、システム既定ブラウザを起動。ランダムな空きloopbackポートでlocalhost/127.0.0.1だけをlistenする。

3.  完了、拒否、タイムアウト、キャンセルを型付き結果へ変換し、listenerを必ず停止する。Microsoftは数分のMFAを考慮して認証専用10分タイムアウト、APIの30秒とは分離する。

4.  成功後にProviderSubjectIdで同一サービス内の重複登録を検査する。重複なら既存アカウントの再認証として扱い、新規行を作らない。Microsoftの既存アカウント再認証は保存済みProviderSubjectIdと結果HomeAccountIdの一致を必須とし、別identityを既存internalAccountId/tokenRefへ関連付けない。

5.  トークンをITokenStoreへ保存してからアカウント設定へtokenRefを書き、カレンダー一覧を取得してPrimaryだけを初期ONにする。

## 8.2 DPAPIエンベロープ

通常設定とトークンを分離する。tokens/{provider}/{internalAccountId}.jsonは方式情報だけが平文で、protectedPayloadの中身はUTF-8 JSONまたはMSAL token cache bytesをDPAPI CurrentUserで暗号化したBase64である。

```text
{
  "schemaVersion": 1,
  "protectionVersion": 1,
  "protectionKind": "dpapiCurrentUser",
  "provider": "google",
  "internalAccountId": "e7a3...",
  "updatedAtUtc": "2026-08-27T03:10:00.0000000Z",
  "protectedPayload": "BASE64_DPAPI_CIPHERTEXT"
}
```

- ProtectedData.Protect/Unprotect(..., DataProtectionScope.CurrentUser)を使用。追加entropyはアプリ固定GUID + provider + protectionVersionから生成し、再インストール後も同一ユーザーなら復号可能にする。

- Google SDKにはFileDataStoreを使わず、ITokenStoreへ接続する独自IDataStoreを渡す。MicrosoftはMSAL token cacheのBeforeAccess/AfterAccessコールバックで不透明バイト列を読み書きする。

- 保存は設定/キャッシュと同じ原子的置換を使う。アクセストークン、リフレッシュトークン、authorization code、Authorization headerをログへ出さない。

- protectionVersionが未知、復号失敗、MSAL provider payloadのdeserialize不能、invalid_grantによるtoken refresh拒否ならトークンファイルを退避し、AccountSyncState=ReauthenticationRequired。アカウント設定と予定cacheは保持する。ITokenStoreのIOException/UnauthorizedAccessException等の一時障害、cancellation、予期しないプログラム例外はtoken破損として隔離しない。

- Microsoftの追加scope同意が必要なconsent_required/interaction_requiredはcache破損ではない。active tokenを隔離せずAuthenticationRequiredへ正規化し、ユーザーが明示的に再認証した場合だけsystem browserを開始する。

- 再認証成功時は同じinternalAccountId/tokenRefを上書きし、そのアカウントだけ同期して復旧する。
- Googleの新規追加と再認証はどちらも`prompt=consent`と`access_type=offline`付きinteractive OAuth結果をメモリ内の一時TokenStoreへ保存する。Google SDKのTokenResponseを復元し、access token、refresh token、有効期間、発行時刻、Bearer token typeが揃った場合だけidentity確定後の永続化へ進む。新規追加はProviderSubjectId取得後に既存アカウントとの重複判定へ渡し、再認証はprimary calendar IDによるidentity照合も成功した場合だけPhase 2のITokenStoreへatomicに置換する。refresh token欠落、キャンセル、失敗、不一致では一時payloadを破棄し、再認証では旧tokenを維持する。

> **移行余地**  将来Credential Manager等へ移る場合はprotectionVersion=2のITokenProtectorを追加し、v1復号→v2再保護を成功後に原子的置換する。CalendarAccountやProvider契約は変更しない。

## 8.3 Microsoft Entra App Registration

- Supported account typesは「任意の組織ディレクトリのアカウント + 個人用Microsoftアカウント」。authorityは`https://login.microsoftonline.com/common`。
- Authentication platformはMobile and desktop applications、Redirect URIは`http://localhost`。Allow public client flowsを有効にする。
- Microsoft Graph / Delegated permissionsへ`Calendars.Read`と`MailboxSettings.Read`を登録する。application permission、write permission、beta専用permissionは追加しない。
- public desktop clientなのでclient secretや証明書を作成・配布しない。client IDは設定/DIから注入し、資格情報JSONや実tokenをGit管理しない。
- Entraの登録時に`User.Read`が既定で存在することがあるが、本Providerが使用する`/me/calendars`等の予定表APIのために`User.Read`を要求する必要はなく、MSALの明示scopeにも含めない。`openid`、`profile`、`offline_access`はOIDC/MSALの認証・キャッシュ動作に伴うscopeであり、`User.Read`というGraph delegated permissionとは別物である。最小権限を維持する場合はApp Registrationから`User.Read`を削除する。残す場合は追加delegated permissionとしてconsent/tokenへ現れ得るため、正式smoke testで実際の付与scopeを監査する。
- 組織Microsoft 365テナントでは、テナントのユーザー同意ポリシーによって`UnifiedCalendar`の`Calendars.Read`と`MailboxSettings.Read`に管理者同意が必要になる場合がある。今回の実サービス検証テナントでは管理者同意が必要であり、同意後はOAuth、Calendar API、masterCategories、silent acquisition、refreshまで正常動作した。これは利用先テナントのEntra ID同意ポリシーによる運用条件であり、組織アカウントで常に管理者同意が必要という意味ではない。導入時は、必要に応じて顧客テナント管理者が`UnifiedCalendar`へ管理者同意を付与する手順を用意する。

## 8.4 Phase 4実サービス検証メモ（2026-08-31）

### 実測事実

- `Calendars.Read.Shared`、`MailboxSettings.Read`、`prompt=consent`、localhost redirectを用いた検証では、authorize URLに`Calendars.Read.Shared`が含まれていても同意画面に当該権限が表示されず、tokenにも付与されず、`GET /me/calendars`は403 ErrorAccessDeniedとなった。既存/新規App Registration、`/common`相当のマルチテナント構成、個人Microsoftアカウント専用`/consumers`構成、空token cacheで再現した。これは検証時に観測した外部挙動であり、根本原因をMicrosoft側の不具合と断定しない。

- `Calendars.Read`へ変更すると、同意画面に読み取り権限が表示され、`Calendars.Read`と`MailboxSettings.Read`がtokenへ付与され、`GET /me/calendars`と`GET /me/outlook/masterCategories`はいずれも200 OKとなった。`.default`、`Calendars.Read.Shared`、write scopeは要求していない。

- 個人Microsoftアカウント間で共有し、受信者がOutlook予定表一覧へ追加済みの状態では、`GET /me/calendars`からowned calendar 2件（Primary 1件）、shared/non-owned calendar 2件、およびowner情報を確定できないcalendar 1件を列挙できた。共有2件について所有情報、read access、canEdit/canShare、color、ProviderLocator用ID等のmetadataを診断上確認し、`GET /me/calendars/{calendar-id}/calendarView`はいずれも200 OK、共有予定1件のall-day、参加情報、元予定URL用情報を確認した。ownership/canEdit/canShareは取得経路の成立を判定するsmoke情報であり、初期版のCoreモデルへ保持しない。共有予定のtimed、recurrence、meeting URLは該当データがなく、このsmokeでは未確認である。

- 正式`UnifiedCalendar` App Registrationと正式client IDによる最終smokeでは、個人Microsoftアカウントに加え、管理者同意後の組織Microsoft 365アカウントでも`/common`と`Calendars.Read` + `MailboxSettings.Read`でOAuthに成功した。組織アカウントでは`GET /me/calendars`で14件を列挙し、全calendarのevent取得と`GET /me/outlook/masterCategories`に成功した。

- 個人・組織アカウントとも、DPAPI token保存、別プロセスでの非対話silent acquisition、access token refreshとrefresh後cache再保存、Graph再同期、PII/secret非出力監査に成功した。

### 設計上の回避方針

製品の必須前提は`Calendars.Read.Shared`の付与可否ではなく、`Calendars.Read`で認証ユーザーのOutlook予定表一覧を`/me/...`経路から読むこととする。`Calendars.Read.Shared`の検証時挙動は外部の既知事項として記録するだけで、製品の正式scopeや実行時fallbackには組み込まない。共有元メールボックスの直接参照が将来必要になった場合は、Microsoftの権限・API挙動を再検証して別フェーズで設計する。

## 8.5 Microsoft token互換性の起点

- `Calendars.Read.Shared`を正式scopeとしていたMicrosoft ProviderはPhase 4開発途中で未リリースであり、実サービス検証でも同scopeはtokenへ付与されなかった。正式ユーザー環境に正常な旧Shared-scope production token/cacheが存在することを前提とせず、当時のMSAL cacheは開発・診断用データとして扱う。
- 旧Shared-scope開発用cacheから`Calendars.Read`へのproduction migrationと、そのcacheを用いた回帰テストはPhase 4 DoDに含めない。正式リリース版のscope互換性の起点は`Calendars.Read` + `MailboxSettings.Read`とする。
- この除外はtoken安全要件を緩和しない。正式scopeのcacheについて、DPAPI保存、silent acquisition、access token refresh、refresh後の再保存、再認証identity固定、identity不一致・cancel・failure時の既存token保持、transient I/O非隔離、consent/interaction requiredの非破損扱い、実際にcorruptまたはinvalidなcacheだけを対象とする隔離を維持する。

## 8.6 Google OAuthクライアントと資格情報の供給（2026-09-06 実サービス検証）

- クライアント種別は**デスクトップ アプリ**。`LocalServerCodeReceiver`を`ForceLoopbackIp`で用い、`http://127.0.0.1:<動的ポート>`で認可コードを受け取る。リダイレクトURIの手動登録は不要で、ウェブ アプリケーション種別では動作しない。scopeは`https://www.googleapis.com/auth/calendar.readonly`のみ。

- **ClientSecretは必須である。** Phase 7c-1の実機検証で、ClientIdだけでは認証が成立せず、Googleがデスクトップ クライアントへ発行するクライアント シークレットの設定を要した。PKCEを用いていてもGoogleのtoken endpointがこれを要求する。8.3のMicrosoftは「public desktop clientなのでclient secretや証明書を作成・配布しない」が、**Googleには当てはまらない**。

- ただしこのシークレットは保護対象の秘密ではない。`GoogleProviderOptions.ClientSecret`のコメントのとおり、デスクトップOAuthクライアントのシークレットはpublic-client metadataであり、機密として守れる前提を置かない。認可の安全性はPKCEとloopback限定listenで担保する。**このシークレットが漏れてもユーザーのカレンダーへはアクセスできない**（認可コードとPKCE verifierが別途必要）。

- 資格情報の供給経路は現状**環境変数だけ**である。`Host.CreateDefaultBuilder`の既定構成により`UnifiedCalendar__Google__ClientId`、`UnifiedCalendar__Google__ClientSecret`、`UnifiedCalendar__Microsoft__ClientId`を読む。リポジトリに`appsettings.json`を置かない方針は維持する。配布物への供給方式は下記8.7のとおり決定した。実装はPhase 8で行う。

- OAuth同意画面の公開ステータスが「テスト」の間は、Googleのrefresh tokenが7日で失効する。

- ~~**公開ステータスは「テスト」のまま出荷する（2026-09-06 決定）。**~~ **2026-09-08 に「本番環境（未検証）」へ変更した。** 当初はテストのまま出荷したが、**利用者ひとりひとりが7日ごとに再認証を求められること**と、**利用者全員をテストユーザーへ個別登録する必要があること**（上限100）が、常駐して黙って表示するというアプリの性質に対して重すぎた。**本番環境へ切り替えれば、審査を通さなくてもこの2つは解消する。**

- **Google Workspace の「内部」は採れなかった。** `lad.co.jp` が Workspace ではないため、ユーザーの種類を「内部」にする選択肢（失効・警告画面・上限・審査のすべてが無くなる）は使えない。**将来 `lad.co.jp` が Workspace になれば、組織下にOAuthクライアントを作り直してClientId/Secretを差し替えるだけで移行できる**（アプリのコード変更は不要）。

### 「本番環境（未検証）」への切り替えで分かったこと（2026-09-08 実施）

**「トグルを切り替えるだけ」ではなかった。** 実際に必要だったのは次である。

- **ブランディングページの必須4項目。** 「アプリを公開」ボタンは、**アプリ名・サポートメール・ホームページURL・プライバシーポリシーURL**が揃うまで非活性のままである（**利用規約URLは要求されない**）。ボタンにホバーすると不足項目が表示される。
- **公開ページ2つ。** GitHub Pages（`lad-furutani.github.io`）にホームページとプライバシーポリシーを置いた。**ホームページはリポジトリの `README.md` をそのまま公開している。**
- **承認済みドメインの登録。** URL欄に入力しただけでは「次のドメインが見つかりません」が消えず、保存できない。**Search Console で所有権を確認したうえで、「ドメインの追加」ボタンから手入力する必要がある。** URL欄からの自動認識だけでは通らなかった。所有権確認は URL プレフィックス方式（HTMLファイル）で行う。**`github.io` は DNS を触れないため「ドメイン」方式は使えない。**
- **ロゴはアップロードしないこと。** ブランディングページに明記のとおり、**ロゴを上げるとブランド確認の審査が発生する。** 未検証で運用する限り空のままにする。

**残る制約。**

- **同意時に「このアプリは Google で確認されていません」の警告画面が出る。** 通過するには**左下の「詳細」→「UnifiedCalendar（安全ではないページ）に移動」→「次へ」**の順に進む必要がある。**目立つ青いボタンは「安全なページに戻る」（中断）**なので、**案内しないと利用者はそこで止まる。** 配布時の案内文に必須。
- **同意済みユーザー数に上限がある**（未検証アプリの上限、100人とされる）。審査を通さない限り引き上げられない。
- 警告画面には「確認されていないアプリでは、ユーザーデータへのアクセス権が**一部失われる可能性があります**」と表示される。**Google が未検証アプリを将来制限しうる。** 制限が現実になった場合は審査（ホームページ・プライバシーポリシー・ロゴ・デモ動画・scope説明が必要）へ動く。**`calendar.readonly` は sensitive scope であり restricted ではないため、第三者機関のセキュリティ評価（CASA）は不要である。**

**未確認の事項。** 切り替え前に取得済みの refresh token が失効するかは実測していない。**7日失効が発行時の状態で決まるなら、切り替え後に一度だけ再認証が必要になる。** 切り替え後8日以上経っても再認証を求められないことをもって、目的の達成とする。

**この事項は実機では当てはまらなくなった（2026-09-09）。** 1.1.0 の受入後に v2 設定を 1.0.0 が隔離したため、**同日 17:15 に3アカウントを再認証した。現在のトークンはすべて切り替え後（本番環境）に発行されたものである。** したがって残る判定は「本番環境で発行した refresh token が7日で失効しないこと」だけであり、**2026-09-17 以降に再認証を求められないことをもって達成とする**（起算日は 2026-09-09 17:15）。

- 公開ステータスはConsoleのトグルでありコード変更を伴わないため、後から変更できる。7日失効を解消したい場合の選択肢は2つある。利用対象がGoogle Workspace組織のアカウントだけであれば**ユーザーの種類を「内部」にする**（失効・警告画面・審査のいずれも無くなる）。個人Gmailを含める必要があるなら**「本番環境」へ公開する**（審査未提出なら「確認されていないアプリ」の警告画面が出て利用者上限100、審査を通せば制限なし。`calendar.readonly`は機微scopeのため、審査には公開ホームページ、プライバシーポリシー、デモ動画、ブランド確認が要る）。

## 8.7 クライアント資格情報の配布方式（2026-09-06 決定）

想定する配布は個人利用と社内利用である。将来の一般公開の可能性は低いが残す。したがって**配布者の資格情報を既定として同梱しつつ、導入先が自前のOAuthクライアントへ差し替えられる**方式とする。

### 供給方式

- **ビルド時にアセンブリへ埋め込む。** 既存の`BuildTimestampUtc`と同じ経路を使う。`UnifiedCalendar.App.csproj`のMSBuildプロパティ →`AssemblyMetadata`属性 → 実行時に`GetCustomAttributes<AssemblyMetadataAttribute>()`で読む。`ApplicationInfoProvider`が同じ形をすでに実装している。新しい仕組みを持ち込まない。

- **既定値は空とし、資格情報が無い環境でもビルドが通るようにする。** `Condition="'$(...)' == ''"`で空を既定にする。値は`build.ps1`が環境変数から読んで渡し、**リポジトリへ入れない**。誰でもクローンしてbuild/testできる状態を保つ。

- **ソースへ直書きしない。** これは秘密だからではない（8.6のとおりpublic-client metadataである）。Googleのクライアントシークレットは`GOCSPX-`という識別可能な形をしており、シークレットスキャンの検出対象である。リポジトリを公開した場合に通知や無効化の対象になり得る。守れない値であることと、リポジトリへ置いてよいことは別である。

- **上書き専用の設定ファイルは作らない。** 環境変数がすでに構成の入口であり、ユーザー環境変数は管理者権限なしで設定でき、組織ではGPOやログオンスクリプトで配布できる。専用ファイルを足すと`AppPaths`の拡張、installerの書き込み処理、アンインストール時の孤児ファイル、`settings.json`との取り違えリスクが増える一方、得るものが無い。**資格情報を入力させるUIも作らない。** 認証まわりに入力欄を増やすとフィッシング的な見え方になり、また「秘密を扱っている」という誤解からマスク表示や暗号化保存へ引きずられる。

### 解決順序

1. 構成（環境変数）
2. アセンブリへ埋め込まれた値
3. どちらにも無ければ**未構成**

- **Googleの`ClientId`と`ClientSecret`は必ず組で解決する。** `ClientId`が構成から来たなら`ClientSecret`も構成から取り、埋め込みへフォールバックしない。片方だけ揃った状態は未構成として扱う。混在させると存在しない組み合わせで認証だけが失敗し、原因が分かりにくい。

- **解決順序と組ルールを1箇所へ閉じる。** `ServiceCollectionExtensions`は解決結果を受け取るだけとし、判断を DI 登録コードへ散らさない。

- **どこから解決されたか（構成 / 埋め込み / 未構成）をログへ1行出す。値は出さない。** 「自前のクライアントが使われない」を切り分ける手段がこれ以外に無い。PII規則には触れない。

### 未構成時の扱いと入れ忘れの検出

Phase 7c-1 の`IAccountRegistrationService.IsProviderAvailable`により、未構成のサービスは追加ボタンが無効になり理由がツールチップへ出る。**資格情報を入れ忘れたビルドは、起動してボタンを見れば分かる。** 静かに壊れない。

上記の組ルールにより、`ClientId`だけ入って`ClientSecret`が空という状態も未構成として現れる。ルールが無いとボタンは有効なのに認証だけ失敗し、発見が遅れる。

最終的な検出は**Inno Setupで生成したinstallerから導入して実際に認証する**ことで行う。

**「静かに壊れない」という上記の前提は、2026-09-07 に実運用で破れた。** 実機受入のF章（ログオン時自動起動）でサインアウト／サインインを行ったため資格情報を入れていたPowerShellセッションの環境変数が消え、そのままビルドして**資格情報が空の署名済みinstallerができた**。ビルド・全テスト・署名・インストール・アンインストールのすべてが成功するため、**気づけるのは導入後にアプリを起動して追加ボタンを見ることと、ログの`Source:"NotConfigured"`の1行だけだった。** ボタンが無効になる仕組み自体は設計どおり働いたが、**出荷工程の中で誰も見ない場所にしか手がかりが無かった。**

**したがって「ビルドスクリプト側での必須チェックは設けない」という判断を撤回する（2026-09-07）。** `build.ps1`は渡されなかった資格情報を警告する（**失敗はさせない**。資格情報の無いマシンでビルドできることは上記の決定である）。`build-installer.ps1`は資格情報の無い発行物に対して**既定で失敗し**、明示のスイッチでのみ続行できる。**判断は現在の環境変数ではなく発行物の内容に基づく**（実行時の環境変数はビルド時の状態と一致しないため、今回の事故がそのまま素通りする）。指示書は`docs/handoff-phase8-credential-build-guard.md`（D98 / D99）。

## 8.8 資格情報とテストユーザーの管理場所

**登録所有者は`lsi.furutani@gmail.com`**（仕様書 TBD-004、18章 P-003）。以下はいずれもこのアカウントでサインインして開く。**コンソールは改装が進んでおり入口が2通りある場合がある。どちらからでも同じ対象に到達する。**

### Google（ClientId と ClientSecret）

| 目的 | URL |
| --- | --- |
| OAuthクライアントの一覧と詳細 | `https://console.cloud.google.com/apis/credentials`<br>または `https://console.cloud.google.com/auth/clients` |
| 同意画面・対象・テストユーザー | `https://console.cloud.google.com/apis/credentials/consent`<br>または `https://console.cloud.google.com/auth/audience` |

**先にプロジェクトを選択すること。** 別プロジェクトの認証情報を見ていると、値はあるのに認証が通らない。

**ClientIdとClientSecretは、クライアント一覧から該当のデスクトップ クライアントを開いた詳細画面に両方表示される。** 種別が**デスクトップ アプリ**であることを確認する（8.6のとおり、ウェブ アプリケーション種別では動作しない）。

**テストユーザーの登録は不要になった（2026-09-08）。** 公開ステータスを「本番環境（未検証）」へ切り替えたため、テストユーザー一覧は参照されない。**一覧は消さずに残しておくこと**（テストへ戻す判断をしたときに再入力せずに済む）。同じページで**公開ステータスの切り替えと承認済みドメインの管理**も行う。

### Microsoft（ClientId のみ）

| 目的 | URL |
| --- | --- |
| アプリの登録の一覧 | `https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade`<br>または `https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade` |

該当アプリの**概要**ページの「アプリケーション (クライアント) ID」がその値である。

**Microsoftはシークレットを使わない。** 8.3のとおりpublic desktop clientとして登録しており、client secretも証明書も作成・配布しない。**`UnifiedCalendar__Microsoft__ClientSecret`という設定は存在しない。**

### 値の渡し方

ビルド時に埋め込む場合（配布物向け、8.7）。

```
$env:UnifiedCalendar__Google__ClientId = "..."
$env:UnifiedCalendar__Google__ClientSecret = "..."
$env:UnifiedCalendar__Microsoft__ClientId = "..."
.\build.ps1
.\build-installer.ps1
```

既に導入済みのアプリへ後から与える場合（**ユーザー環境変数にすること**。スタートメニューから起動したアプリはセッション変数を引き継がない）。

```
[Environment]::SetEnvironmentVariable('UnifiedCalendar__Google__ClientId','...','User')
```

**どちらの場合も、導入後にログで解決元を確認すること。** 埋め込みなら`Embedded`、環境変数なら`Configuration`になる。

```
Get-Content "$env:LOCALAPPDATA\UnifiedCalendar\logs\*.log" -Tail 50 | Select-String "OAuthClientCredentialsResolved"
```

**値そのものはログに出ない**（D82）。出るのは解決元の列挙値だけである。
