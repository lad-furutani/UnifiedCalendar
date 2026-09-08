# 7. Google / Microsoft Provider設計

## 7.1 共通Provider契約

```text
Task<AuthAccountResult> AuthenticateAsync(CancellationToken ct);
Task<AuthAccountResult> ReauthenticateAsync(
    CalendarAccount account, CancellationToken ct);
Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
    CalendarAccount account, CancellationToken ct);
Task<ProviderCalendarResult> GetEventsAsync(
    CalendarAccount account, CalendarDescriptor calendar,
    TimeRangeUtc range, CancellationToken ct);
```

ProviderCalendarResultは成功時に全ページを取り込んだIReadOnlyList<CalendarEvent>を返し、失敗時はProviderErrorCategory、HTTP状態、RetryAfter、再認証要否を返す。SDK例外は境界で分類し、Coreへ例外型を漏らさない。

## 7.2 Google Provider

- Google.Apis.Calendar.v3とGoogle.Apis.Authを使用する。OAuthクライアント種別はDesktop app。Google CloudのOAuth consentは開発・検証中Testing、公開時に検証手続きを行う。

- スコープはhttps://www.googleapis.com/auth/calendar.readonlyの1つ。アクセス可能なカレンダー一覧と予定本文を読み取れ、書込み権限を含まない。

- CalendarList.ListをnextPageTokenがなくなるまで取得。primary=trueだけを初期ON、追加・共有・購読カレンダーも選択候補へ含める。

- Events.ListはCalendarIdごとにTimeMin/TimeMax、SingleEvents=true、ShowDeleted=false、OrderBy=StartTimeを指定し、全nextPageTokenを追う。API件数上限による打切りを設けない。

- イベント色はEvent.ColorId→Colors.Eventsを最優先、なければCalendarListEntry.BackgroundColor、最後に共通既定色。

- cancelledを除外。本人参加状態はattendees.selfのresponseStatusから変換。HtmlLinkを元予定URL、ConferenceData.EntryPointsまたはHangoutLinkを会議URLへ正規化する。

- Googleの説明はHTMLを描画せず、小さなHtmlToPlainTextConverterでタグ除去、改行正規化、WebUtility.HtmlDecodeを行う。
- Googleのinteractive OAuthは、新規追加・既存アカウント再認証の両方で認可要求に`prompt=consent`と`access_type=offline`を指定してrefresh tokenを取得する。tokenを一時メモリへstageし、実TokenResponseのaccess token、refresh token、正の有効期間、発行時刻、Bearer token typeを検証する。新規追加ではその後にProviderSubjectIdを確定して重複判定へ渡し、再認証ではprimary calendar IDが保存済みProviderSubjectIdと一致した場合だけITokenStoreへcommitする。refresh token欠落、キャンセル、OAuth失敗、identity不一致では不完全なtokenを永続化せず、再認証時は旧tokenを維持する。

## 7.3 Microsoft Provider

- Microsoft.Graph安定版SDKとMicrosoft.Identity.Clientを使用し、Graph v1.0を明示する。beta専用プロパティに依存しない。

- アプリ登録のSupported account typesは『任意の組織ディレクトリ + 個人Microsoftアカウント』、authorityは/common、public client、redirectはhttp://localhost。

- 製品が明示的に必須要求するMicrosoft Graphの委任スコープは`Calendars.Read`と`MailboxSettings.Read`。前者で`/me/calendars`から列挙できるカレンダーとその予定を読み取り、後者で`/me/outlook/masterCategories`のカテゴリ色を読み取る。write scopeは要求しない。`openid`、`profile`、`offline_access`はOIDC/MSALの認証・キャッシュ動作に伴うscopeで、製品のGraph delegated permissionとは区別する。`User.Read`は明示scopeにも必須Graph権限にも含めない。

- 正式リリース版は通常同期のAcquireTokenSilentへ`Calendars.Read`と`MailboxSettings.Read`を渡す。`Calendars.Read.Shared`検証時の開発用cacheは製品互換性の起点にせず、そのcacheから正式scopeへのproduction migrationを保証しない。正式scopeでのconsent_required/interaction_requiredはtoken破損として隔離せず、AuthenticationRequiredとして明示的な再認証へ誘導する。
- silent acquisitionのMSAL例外はcatch型の順序ではなくErrorCodeとUiRequiredExceptionClassificationを併用して分類する。MsalUiRequiredExceptionでも、ConsentRequired分類およびconsent_required、interaction_required、login_requiredは隔離しない。それ以外のinvalid_grant、refresh_token_expired、refresh_token_revokedは再利用不能として隔離する。

- 再認証はCalendarAccount.ProviderSubjectIdを期待identityとして渡す。cache内の同じHomeAccountIdをWithAccount、なければlogin hintで絞り、結果のHomeAccountIdを再検証する。不一致は成功にせず、同じinternalAccountId/tokenRefへ別identityを関連付けない。成功後は選択identity以外のMSAL accountをremoveし、tokenRefあたり1 Microsoft identityとする。新規追加では期待identityを指定せず任意のMicrosoftアカウントを選択できる。

- カレンダー列挙の正式経路はGraph v1.0の`GET /me/calendars`。`@odata.nextLink`がなくなるまで全ページを追い、所有・購読・共有カレンダーを列挙してcalendar ID、表示名、Primary、製品動作用のread可否、colorを既存のCalendarDescriptorへ写像する。`ownership`、`canEdit`、`canShare`は実サービスsmokeの診断情報として確認できるが、初期版ではCalendarDescriptor、ProviderLocator、設定、cacheへ保持しない。

- イベント取得の正式経路はGraph v1.0の`GET /me/calendars/{calendar-id}/calendarView`。startDateTime/endDateTimeを指定し、`@odata.nextLink`がなくなるまで追う。`Prefer: IdType="ImmutableId"`と`Prefer: outlook.timezone="UTC"`を付ける。

- 初期版で共有カレンダーとしてサポートするのは、認証ユーザー自身のOutlook予定表一覧へ追加済みで、`/me/calendars`から列挙できるものに限る。目的の共有カレンダーが列挙されない場合は、ユーザーにOutlook側で共有を受諾・追加してから一覧を再取得してもらう。共有元メールボックスを`/users/{owner}/...`で直接参照する経路、およびOutlook予定表一覧へ未追加の共有・委任カレンダーの直接探索は初期版の対象外とする。

- isCancelled=trueを除外。responseStatus.responseを共通参加状態へ、webLinkを元予定URL、onlineMeeting.joinUrlまたはonlineMeetingUrlを会議URLへ変換する。

- 色はイベントに割り当てられたcategoriesの先頭から、masterCategoriesで色へ解決できる最初のものを優先し、なければcalendar.color、最後に共通既定色。Outlookのpreset0～24はGraph公式のRed～DarkCranberry系列を維持する固定近似RGBへ変換する。

- masterCategoriesは予定本文に対する補助情報である。403（追加permission未同意またはtenant制約）、404、rate limit、一時network/timeout/5xx、malformed responseでは安全な分類だけを診断し、その同期回はカテゴリ色なしで予定を返してcalendar.colorへフォールバックする。401/AuthenticationRequiredとcaller cancellationは予定同期全体の失敗として扱う。

- 本文はPrefer: outlook.body-content-type=textで取得し、なおHTMLが返った場合だけ共通プレーン化処理を通す。

## 7.4 日付範囲とページングの境界

対象範囲はPresentation上のローカル今日0:00からN日後0:00まで。ただし過去開始で継続中の時刻指定予定を含めるため、Providerの取得開始は前回キャッシュ中の最古の未終了開始時刻と今日0:00の早い方にする。キャッシュがない初回は今日0:00から取得し、APIが返す現在進行中の長時間予定を取りこぼさないよう、各サービスのcalendarView/Eventsクエリで終了境界との重なりを取得する。Providerは範囲重複条件を使い、単なるstart>=fromフィルタにしない。
