# 18. 製品判断TBDと初期版対象外

実装方式は本書で確定済み。残すTBDは、製品として人が見て決める値または外部ポータル上の公開判断だけとする。

| TBD | 決定内容 | 実装上の扱い |
| --- | --- | --- |
| ~~P-001 製品識別~~ | **決定済（2026-09-06）。** 正式製品名・exe名・`%LOCALAPPDATA%`のトップフォルダ名はいずれも`UnifiedCalendar`とし、変更しない。 | `AppIdentity`の定数をそのまま用いる。変更が無いため既存の設定・トークンはそのまま引き継がれる。 |
| ~~P-002 ビジュアル~~ | **決定済（2026-09-06）。** 正式アイコンを受領した。16/24/32/48/64/96/128/256pxの8サイズを32bppで含む単一の`.ico`。 | Phase 8でアプリ・トレイ・インストーラへ適用する。公式G/Mロゴを一覧に使わない方針は変更しない。**トレイの16px表示は実機で確認する。** |
| ~~P-003 OAuth登録~~ | **決定済（2026-09-07）。登録所有者は`lsi.furutani@gmail.com`。** Google の公開ステータスは**2026-09-08 に「本番環境（未検証）」へ変更**した（出荷時は「テスト」。7日ごとの再認証とテストユーザーの個別登録を解消するため。**審査は通していない。** 切り替えに必要だった作業と残る制約は`08-oauth-and-token-storage.md` 8.6）。検証（審査）へ移す時期は定めない。本番client IDの値そのものはリポジトリへ入れない。 | **ビルド時にアセンブリへ埋め込み、環境変数で上書きできる**（8.7、Phase 8-1で実装）。secretをrepoへcommitしない原則は維持する。実機受入で埋め込み経路によるアカウント登録を確認済み（`docs/acceptance-phase8.md` B 章）。 |
| ~~P-004 UI実機値~~ | **決定済（2026-09-07）。現行の`LayoutMetrics` / `ColorMetrics`の値で1.0.0を確定する。** Phase 6 / 7a / 7b / 7c / 7d / 8 の実機受入をすべて現行値で通過しており、変更を要する指摘は出なかった。ダークモードの詳細ポップアップの区切り線（既定`Separator`）も実機で問題とならなかったため手を入れない。 | **唯一の例外は進行中予定の進捗明度差**で、WCAG AAを下回ることを既知の制限として判断のうえ出荷する（理由と見直し条件は`11-presentation-pipeline.md` 11.3）。実機確認の記録は`docs/acceptance-phase8.md` D 章。 |
| ~~P-005 配布表示~~ | **決定済（2026-09-06）。** Publisher名は`Logic and Design Inc.`。Setup表示名はすべて`UnifiedCalendar`とし、和名を併記しない（`AppName` / `UninstallDisplayName` = `UnifiedCalendar`、スタートメニューはフォルダを作らず直接ショートカット、`DefaultDirName` = `{autopf}\UnifiedCalendar`）。コード署名は導入済みの証明書をInno Setupの`SignTool=MySignTool` / `SignToolRetryCount=2` / `SignedUninstaller=yes`で用いる。 | **`MySignTool`という名前はInno Setup IDEの Tools > Configure Sign Tools に登録された実コマンドへ解決される。この登録はマシンローカルでありリポジトリには入らない。** 資格情報（8.7）と同じ原則で、リポジトリは参照だけを持ち実体を持たない。スクリプトからビルドする場合は`ISCC.exe /SMySignTool="<コマンド>"`で定義を渡せる。**署名環境が無いマシンでもインストーラをビルドできるよう、署名ディレクティブは前処理条件で括る**（例: `/DSIGN`を渡したときだけ有効）。<br>**追加決定（2026-09-07）。** `PrivilegesRequired`は**`lowest`（ユーザー単位）**とする。データ（`%LOCALAPPDATA%`）、トークン（DPAPI CurrentUser）、自動起動（HKCU Run）がすべてユーザー単位に閉じており、マシン単位で入れると**昇格したアンインストーラが別アカウントとして動きうるためログオン中ユーザーのHKCU自動起動エントリを確実に消せない**（存在しないexeを指す自動起動が残る）。`PrivilegesRequiredOverridesAllowed`は使わない（アップグレード時に前回と違う側を選んで二重に入る事故を避ける）。アンインストール時、**`%LOCALAPPDATA%\UnifiedCalendar`は削除しない**（入れ直せば設定・アカウント登録が戻るのが期待動作）が、**HKCUの自動起動値は削除する**。インストーラのオプションは**「インストール後に起動」のみ**とし、デスクトップショートカットと自動起動設定は置かない（自動起動は設定画面H-5が唯一の管理者であり、二重管理にしない）。<br>**署名範囲の決定（2026-09-07）。** `[Setup]`の`SignTool`が署名するのは**インストーラ本体とアンインストーラだけ**であり、`[Files]`で同梱するバイナリは署名されない。発行物530個のうち16個（自社ビルド7個、発行元が署名していない第三者9個＝Serilog系5・Google.Apis系4）が未署名だったため、**`[Files]`に`signonce`フラグを付けて16個すべてを署名する**。ファイル名は列挙せず、Microsoft署名済みの514個は`signonce`の判定で自動的に対象外とする。第三者9個も署名対象に含める（Authenticodeの署名は著作の表明ではなく配布者の同一性と非改竄の表明であり、再配布物への署名は正当）。**`SignTool`指令が無い状態で`sign`/`signonce`を書いてもエラーにならず黙ってスキップされる**ことをISCCで実測済みのため、`[Files]`を`#ifdef SIGN`で二重化する必要はない。ただし**「ビルド成功」が「署名済み」を意味しない**ので、ビルドスクリプト側で署名を検証する。ビルドスクリプトは**既定で署名し**、`-NoSign`で署名なし、`-Quiet`のときだけISCC出力を`/Qp`で抑制する。詳細は`docs/handoff-phase8-3-remediation.md`。 |

## 18.1 初期版へ追加しないもの

通知、予定編集、印刷/エクスポート、検索/絞り込み、重複統合、Push/Webhook、自動アップデート、右クリック、予定行drag、行件名copy、祝日取得、ネットワーク復帰event監視、常時最前面/AppBar/自動dock、help/tutorial、起動中設定file監視、詳細log level設定は実装しない。Microsoft共有calendarはOutlook予定表一覧へ追加済みで`/me/calendars`から列挙できるものまでとし、`/users/{owner}/...`による共有元メールボックス直接参照と未追加の共有・委任calendarの直接探索は初期版へ追加しない。

## 18.2 同梱物のライセンス（2026-09-07 調査）

**同梱物はすべて MIT または Apache-2.0 である。** コピーレフト（GPL / LGPL）も、再配布を制限する商用ライセンスも無く、**配布形態（self-contained・社内配布・有償無償）に制約は生じない。** `.nuspec` と `LICENSE.txt` を実読し、発行アセンブリから著作権表示を採取して確認した。

| 区分 | ライセンス |
| --- | --- |
| .NET 8ランタイムおよびライブラリ（self-containedの同梱分）、`Microsoft.Extensions.*`、`Microsoft.Graph` / `.Core`、`Microsoft.Kiota.*`、`Microsoft.Identity.Client`、`Microsoft.IdentityModel.*`、`Azure.Core`、`System.Security.Cryptography.ProtectedData` | MIT |
| `CommunityToolkit.Mvvm`、`Newtonsoft.Json` | MIT |
| `Google.Apis` / `.Auth` / `.Core` / `.Calendar.v3`、`Serilog` ほか4件、`Std.UriTemplate` | Apache-2.0 |

**MIT も Apache-2.0 も著作権表示とライセンス本文の添付を求めるため、`THIRD-PARTY-NOTICES.txt` を配布物へ同梱する。** 手書きし、配布されるパッケージが載っていないときに落ちるテストでズレを検知する。利用者からの見つけやすさはインストール先に置くまでとし、**アプリのUIには手を入れない**（2026-09-07 決定）。指示書は`docs/handoff-phase8-third-party-notices.md`（D100〜D102）。

**対応不要と判断したもの。** Apache-2.0 §4(d)のNOTICE伝播は、該当10パッケージのいずれにも`NOTICE`ファイルが存在しないため発生しない。§4(b)の変更告知はコードを変更していないため不要。§6の商標は公式ロゴを使わない方針（P-002）で満たす。**ただし第三者バイナリ9件へAuthenticode署名を付加しているため、「コードは変更せず署名のみを付加した」旨を表示ファイルへ明記して解釈の余地を残さない。**

**ソフトウェアライセンスとは別枠の義務が2件ある。** Google APIの利用規約とUser Data Policyは、公開ステータスを「本番環境」へ移す段階でプライバシーポリシー等を要する（8.6）。**UnifiedCalendar本体のライセンス／EULAは未定義**であり、インストーラに同意画面も無い。社内配布では明示を要さないが、外部配布へ広げる場合は事業判断が必要になる。

## 18.3 実装着手条件

- P-001～P-005はPhase 8まで仮値で進められる。Google/Microsoftの実接続試験だけは対応client IDが必要。

- 仕様の追加・変更が発生した場合は、基準仕様書のrequirement ID、本書の影響component、ACC matrix、schemaへの影響を同時に更新する。

- 本書のTBD以外を『実装時に決める』へ戻さない。合理的な差異が見つかった場合は、独断で機能変更せず設計差分として提示する。
