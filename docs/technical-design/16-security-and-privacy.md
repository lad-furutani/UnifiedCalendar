# 16. セキュリティ・プライバシー

## 16.1 認証・通信

- OAuth Authorization Code + PKCEを公式ライブラリに任せ、システムブラウザとloopback callbackだけを使う。embedded browser、OOB copy/paste、password grantは使わない。

- Microsoftはpublic client、GoogleはDesktop client。配布物に含まれるclient ID/desktop client secretを機密鍵として扱わず、サーバー秘密情報を持たせない。

- 製品が明示的に要求するscopeはGoogle calendar.readonly、Microsoft `Calendars.Read` + `MailboxSettings.Read`。Microsoftの後者はmasterCategoriesの参照だけに使用する。OIDC/MSALの`openid`、`profile`、`offline_access`とGraph delegated permissionを区別し、`User.Read`、write scopeを必須要求せず、Graph betaへ接続しない。

- Microsoft Entra登録はpublic desktop client、`/common`、`http://localhost`、Allow public client flows有効とし、client secretを作成しない。追加scope同意のconsent_required/interaction_requiredはtoken破損と区別し、明示的な再認証へ誘導する。

- Microsoft再認証は保存済みProviderSubjectIdとHomeAccountIdを照合し、成功後に非対象MSAL accountをcacheから除去する。tokenRefあたり1 identityとし、別identityの選択を既存internalAccountIdへ反映しない。
- Googleの新規追加・再認証は`prompt=consent`とoffline accessで長期refresh tokenを取得する。OAuth tokenはTokenResponseの長期利用要件を満たすまでメモリ内だけにstageする。再認証ではさらに保存済みProviderSubjectIdとprimary calendar IDを照合し、refresh token欠落、別identity、キャンセル、失敗時は旧tokenを変更しない。

- HTTPはTLSのSDK既定検証を使用し、証明書検証を無効化しない。redirect listenerはloopbackだけ、認証完了/取消で閉じる。

## 16.2 ローカルデータ

- OAuthトークンはDPAPI CurrentUser。Windows別ユーザーや別PCへコピーしても復号できない。

- 設定とキャッシュは平文JSON。WindowsユーザープロファイルACLの範囲を超える暗号化・盗難対策は初期版で提供しない。

- アカウント削除はtoken、account settings、calendar selection、専用cacheを対象IDで削除し、他アカウントへ触れない。削除後のrecovery backupにも対象トークンを残さない。

- URI起動はUri.TryCreateのabsolute http/httpsだけ。file、javascript、data、shell特殊文字列をProcessStartInfoへ渡さない。

- WPFはHTMLをレンダリングせずplain textのみ表示する。URLは分離してHyperlink化し、クリック対象以外の文字列を実行しない。

## 16.3 脅威と残余リスク

| リスク | 対策 / 残余 |
| --- | --- |
| ローカル同一ユーザーのマルウェア | DPAPI CurrentUserでも同一ユーザー権限の攻撃は防げない。予定cacheも平文。製品説明で明示。 |
| token破損/移行失敗 | 方式識別、atomic write、quarantine、再認証。cache表示は維持。 |
| ログへのPII混入 | 安全なproperty whitelist、hash、例外sanitizer、HTTP body/header禁止。テストでメール/件名fixture非出力を検査。 |
| 悪意あるURL | http/https absoluteのみ。OS既定browserへ渡し、アプリ内実行しない。 |
| 共有calendar権限変化 | 次回同期で403/404を該当calendar失敗として保持し、前回cacheとwarningを表示。 |
