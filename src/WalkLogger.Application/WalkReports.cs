using WalkLogger.Core;

namespace WalkLogger.Application;

public static class WalkReports
{
    public static string AnnualReport(IEnumerable<WalkSession> sessions, int year)
    {
        var walks = sessions.Where(w => !w.IsDemo && w.Points[0].Time.ToLocalTime().Year == year).ToList();
        var loops = walks.Where(w => w.CompletedLoop).ToList();
        var fastest = loops.Count == 0 ? "確認済みの一周記録なし" :
            WalkAnalysis.FormatDuration(loops.Min(w => w.Stats.Elapsed));
        return $"""
            # {year}年の街歩きアーカイブ

            - 街歩き: {walks.Count}回
            - 一周完了（ユーザー確認済み）: {loops.Count}回
            - 実測区間の総距離: {walks.Sum(w => w.Stats.DistanceKm):F2} km
            - 合計経過時間: {WalkAnalysis.FormatDuration(TimeSpan.FromTicks(walks.Sum(w => w.Stats.Elapsed.Ticks)))}
            - 一周の最短経過時間: {fastest}
            - 撮影枚数: {walks.Sum(w => w.Photos.Count)}枚

            サンプルを除外。欠測区間の距離は含みません。
            新規スポット数や店舗の変化はGPSだけでは確定できないため集計しません。

            {string.Join(Environment.NewLine, walks.Select(w => $"- {w.DisplayDate} / {w.Title} / {w.DisplayStats}"))}
            """;
    }
}
