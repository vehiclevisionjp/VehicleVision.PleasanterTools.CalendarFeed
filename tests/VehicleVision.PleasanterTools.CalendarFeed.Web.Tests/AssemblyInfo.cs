// **テストクラスを並列に走らせない。** ParameterFilesTests はプロセス全体の環境変数を書き換えるため、
// 並列だと、同時に起動している別のテスト（アプリは環境変数も読む）の設定へ紛れ込む（実際に踏んだ）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
