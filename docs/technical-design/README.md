**IMPLEMENTATION HANDOFF**

# UnifiedCalendar 技術設計書

Google Calendar / Microsoft 365 統合・参照専用 Windows 常駐アプリ

**文書目的:** Codex が実装へ着手できる設計・境界・完了条件の確定

**基準文書:** Windows向けスケジュール表示ツール 仕様書 v1.0（2026-08-27）

**コード名:** UnifiedCalendar

**対象:** Windows 11 x64 / C# / WPF / .NET 8 LTS

**設計原則:** 個人向け常駐アプリとして、保守性と実装容易性を優先

**初期正式版:** 1.0.0（Semantic Versioning）

> **設計の結論**  UI、同期、表示ルール、Provider、ファイルI/Oを明確に分ける。ただしプロジェクト数は6つに留め、抽象化は外部API・OS・永続化・時刻・UIスレッドの境界にだけ置く。

> **重要な差分**  基準仕様書の後に確定した判断により、OAuthトークンだけはDPAPI CurrentUserで暗号化する。通常設定と予定キャッシュは平文JSONのままとし、機能範囲は変更しない。

> 文書版 1.1  開発環境互換性のため .NET 8 LTS へ変更。仕様・アーキテクチャ・機能範囲に変更はない。

> **Phase 4確定差分（2026-08-31）**  Microsoftの明示的な必須委任権限を`Calendars.Read` + `MailboxSettings.Read`とし、`/me/calendars`および`/me/calendars/{calendar-id}/calendarView`を正式経路とする。初期版の共有カレンダー対応は、認証ユーザーのOutlook予定表一覧へ追加済みで`/me/calendars`から列挙できるものに限る。

> **参照優先順位**  以後の実装・レビューでは本ディレクトリのMarkdownを優先する。元の`UnifiedCalendar_技術設計書_v1.1.docx`は変換元として保持し、Markdownへ反映した後続判断とは自動同期しない。

# 文書構成

- [1. 設計目的・前提と基準仕様書との関係](01-purpose-and-precedence.md)
- [2. 確定技術スタックとアーキテクチャ原則](02-technology-and-architecture.md)
- [3. ソリューション / プロジェクト構成](03-solution-structure.md)
- [4. レイヤ / コンポーネント責務](04-component-responsibilities.md)
- [5. 主要クラス・インターフェースと依存関係](05-classes-and-dependencies.md)
- [6. 共通データモデル](06-common-data-model.md)
- [7. Google / Microsoft Provider設計](07-providers.md)
- [8. OAuth認証・トークン保存設計](08-oauth-and-token-storage.md)
- [9. 同期シーケンス、部分成功、リトライ、キャンセル、レート制限](09-sync-and-failure-control.md)
- [10. 設定・キャッシュJSON、原子的保存、スキーマ移行](10-settings-cache-schema.md)
- [11. Presentation pipeline（フィルタ→グループ→ソート→色→進捗→差分）](11-presentation-pipeline.md)
- [12. WPF / MVVM構成、Dispatcher方針、UI virtualization](12-wpf-mvvm-dispatcher-virtualization.md)
- [13. ウィンドウ・トレイ・単一インスタンス・DPI](13-window-tray-single-instance-dpi.md)
- [14. ログ設計](14-logging.md)
- [15. テスト戦略とACC-001～ACC-012マトリクス](15-testing-and-acceptance.md)
- [16. セキュリティ・プライバシー](16-security-and-privacy.md)
- [17. Codex向け実装フェーズ分割とDefinition of Done](17-implementation-phases.md)
- [18. 製品判断TBDと初期版対象外](18-product-tbd-and-out-of-scope.md)
- [付録 A. 確定デフォルト / 実装定数](appendix-a.md)
- [付録 B. 技術参照（2026-08-27確認）](appendix-b.md)

> 原文: `UnifiedCalendar_技術設計書_v1.1.docx`。実装時の部分参照を容易にするため章単位へ分割し、その後に確定した設計判断はMarkdownへ反映する。
