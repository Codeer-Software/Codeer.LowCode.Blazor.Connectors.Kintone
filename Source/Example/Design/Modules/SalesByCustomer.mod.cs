//受注を 顧客 × 進捗 でまとめ、合計金額の多い 5 社を出す。
//2 つ目の軸を列にする (Show の第 2 引数 = 行にする軸の数)
void DetailLayoutDesign_OnAfterInitialization()
{
    var agg = new ModuleAggregator<Order>();
    agg.GroupBy(e => e.Customer);
    agg.GroupBy(e => e.Stage);
    var total = agg.Sum(e => e.Total);
    agg.OrderByMeasureDescending(total);
    agg.Limit(5);
    Sales.Show(agg, 1);
}
