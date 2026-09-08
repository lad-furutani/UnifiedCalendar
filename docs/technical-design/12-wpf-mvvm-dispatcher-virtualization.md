# 12. WPF / MVVM構成、Dispatcher方針、UI virtualization

## 12.1 View / ViewModel

| View | ViewModel | 境界 |
| --- | --- | --- |
| MainWindow | MainWindowViewModel | 固定status、sticky date overlay、仮想化Timeline、空/取得中/未登録state。 |
| EventDetailsPopup | EventDetailsViewModel | 常に1つ。文字選択・URL・元予定。Placement/DPI/EscはView側behavior。 |
| SettingsWindow | SettingsWindowViewModel | モードレス1つ、6カテゴリ、OK/Apply/Cancel、未適用変更の編集コピー。 |
| ColorRuleEditor | ColorRuleEditorViewModel | 検証、条件、順序、固定サンプルpreview。 |

- [ObservableProperty]と[RelayCommand]/[AsyncRelayCommand]を使用し、code-behindは純UIイベントとフォーカス/Placementだけに限定する。

- 画面文字列はResources/Strings.ja-JP.resxへ置き、IUiTextService経由で書式化する。View/ViewModelへ日本語文字列を直書きしない。将来Strings.en-US.resxを追加できる。

- 初期UIカルチャはja-JP固定、日付は24時間・日本語曜日。データ比較や保存はInvariantCulture/ISO形式。

## 12.2 Dispatcher

- API、ページング、再試行待機、JSON I/O、スナップショット構築、色/ソートはバックグラウンドで行う。Task.RunはCPU投影がUIスレッドから呼ばれる場合にだけ使い、async I/OをTask.Runで包まない。

- `ICalendarSyncService.SnapshotChanged`と`AccountStateChanged`はバックグラウンドスレッドから発火する可能性がある。`ICalendarSyncService`はUI thread affinityおよび`SynchronizationContext`へのマーシャリングを保証しない。

- App/UI層の購読側は、ViewModel propertyや表示用collectionへ反映する前に`IUiDispatcher`を介してWPF Dispatcherへマーシャリングする。Phase 6 UIはこの契約に従い、CoreからWPF Dispatcherへ依存させない。

- ObservableCollection、ViewModel property、フォーカス、ScrollViewer、Popup、Windowの変更だけIUiDispatcher経由でUIスレッドへ戻す。

- DispatcherPriority.DataBindで差分を1回の論理batchとして適用し、同時適用をSemaphoreSlim(1,1)で直列化する。

## 12.3 仮想化と1,000件性能

- ListBox + VirtualizingStackPanel、IsVirtualizing=true、VirtualizationMode=Recycling、ScrollUnit=Pixel、CanContentScroll=true。外側にScrollViewerを置かない。

- 日付ヘッダーもフラットitemsとして描画し、status直下に別のsticky overlayを置く。ScrollChanged時に最上位の可視日付キーだけを更新する。

- DataTemplateはEventTriggerや重いconverterを避け、表示派生値をViewModelで事前計算。BrushはArgb値ごとの小さなcacheを使う。

- 初期目標: 1,000件スナップショットで初回投影と差分適用が各200ms以内、連続スクロール・詳細表示・フォーカス移動に体感的な引っ掛かりがない。

- API取得件数は上限なし。ページングは全件完了まで行い、UIは仮想化で描画要素数を抑える。

## 12.4 密度とフォント

- フォントはYu Gothic UI、初期14 DIP、10～24 DIP。

- 密度はcompact/standard/comfortable。固定行高を持たず、font line height + icon max size + 上下padding + separatorからMinHeightを算出する。

- 時刻列幅は現在のFontFamily/FontSize/DPIで『10:00–11:00』『終日』『終日  8/27～8/29』の測定結果に左右paddingを加え、全行で共有する。
