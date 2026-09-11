using Umbra.Core;

namespace Umbra.Tests;

public class HistoryTests : IDisposable
{
    private readonly string _tempDir;

    public HistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "umbra-tests-" + Guid.NewGuid());
        Config.DataDir = _tempDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static long DaysAgo(int n, int hour = 12)
    {
        var d = DateTime.Now.Date.AddDays(-n).AddHours(hour);
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }

    private static long AtHour(int hour, int daysAgoCount = 0)
    {
        var d = DateTime.Now.Date.AddDays(-daysAgoCount).AddHours(hour);
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }

    private static HistoryEntry Entry(long endedAt, string questName, double focusedMinutes) => new()
    {
        EndedAt = endedAt,
        Kind = "custom",
        HardMode = false,
        QuestName = questName,
        FocusedMinutes = focusedMinutes,
    };

    [Fact]
    public void GetStats_TodayMinutesOnlyCountsEntriesFromToday()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "a", 25),
            Entry(DaysAgo(1), "b", 40),
        });
        var stats = History.GetStats();
        Assert.Equal(25, stats.TodayMinutes);
        Assert.Equal(2, stats.TotalSessions);
    }

    [Fact]
    public void GetStats_StreakCountsConsecutiveDaysEndingToday()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "a", 10),
            Entry(DaysAgo(1), "a", 10),
            Entry(DaysAgo(2), "a", 10),
        });
        Assert.Equal(3, History.GetStats().StreakDays);
    }

    [Fact]
    public void GetStats_StreakStillCountsIfTodayEmptyButYesterdayHasEntries()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(1), "a", 10),
            Entry(DaysAgo(2), "a", 10),
        });
        Assert.Equal(2, History.GetStats().StreakDays);
    }

    [Fact]
    public void GetStats_StreakResetsToZero_WhenThereIsAGap()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "a", 10),
            Entry(DaysAgo(3), "a", 10), // trou de 2 jours
        });
        Assert.Equal(1, History.GetStats().StreakDays);
    }

    [Fact]
    public void GetStats_EmptyHistory_GivesZeroedStats()
    {
        History.Save(new List<HistoryEntry>());
        var stats = History.GetStats();
        Assert.Equal(0, stats.TodayMinutes);
        Assert.Equal(0, stats.WeekMinutes);
        Assert.Equal(0, stats.MonthMinutes);
        Assert.Equal(0, stats.AverageSessionMinutes);
        Assert.Equal(0, stats.StreakDays);
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.TotalMinutes);
        Assert.Equal(0, stats.BestDayMinutes);
        Assert.Equal(0, stats.LongestSessionMinutes);
    }

    [Fact]
    public void GetStats_ComputesSimpleLifetimeRecords()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "a", 25),
            Entry(DaysAgo(0), "b", 15),
            Entry(DaysAgo(1), "a", 30),
        });

        var stats = History.GetStats();
        Assert.Equal(70, stats.TotalMinutes);
        Assert.Equal(40, stats.BestDayMinutes);
        Assert.Equal(30, stats.LongestSessionMinutes);
    }

    [Fact]
    public void GetStats_MonthMinutes_OnlyCountsCurrentCalendarMonth()
    {
        var now = DateTime.Now;
        var lastMonth = new DateTime(now.Year, now.Month, 1).AddMonths(-1).AddDays(14).AddHours(12);
        History.Save(new List<HistoryEntry>
        {
            Entry(new DateTimeOffset(now).ToUnixTimeMilliseconds(), "a", 30),
            Entry(new DateTimeOffset(lastMonth).ToUnixTimeMilliseconds(), "a", 99),
        });
        Assert.Equal(30, History.GetStats(now).MonthMinutes);
    }

    [Fact]
    public void GetStats_AverageSessionMinutes_AveragesLast30DaysOnly()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "a", 20),
            Entry(DaysAgo(1), "a", 40),
            Entry(DaysAgo(60), "a", 999), // hors fenetre
        });
        Assert.Equal(30, History.GetStats().AverageSessionMinutes);
    }

    [Fact]
    public void Append_IgnoresSessionsUnderThreshold()
    {
        History.Save(new List<HistoryEntry>());
        History.Append("custom", false, "instant", 0.05);
        Assert.Empty(History.Load());

        History.Append("custom", false, "real", 5);
        Assert.Single(History.Load());
    }

    [Fact]
    public void GetQuestBreakdown_AggregatesByQuestName_SortedDescending()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "Thèse", 30),
            Entry(DaysAgo(1), "Thèse", 90),
            Entry(DaysAgo(1), "Coréen", 45),
        });
        var breakdown = History.GetQuestBreakdown(7);
        Assert.Equal(new[] { ("Thèse", 120), ("Coréen", 45) }, breakdown.Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void GetQuestBreakdown_EmptyQuestName_FallsBackToDefaultLabel()
    {
        History.Save(new List<HistoryEntry> { Entry(DaysAgo(0), "", 20) });
        var breakdown = History.GetQuestBreakdown(7);
        Assert.Equal(new[] { ("Session de focus", 20) }, breakdown.Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void GetQuestBreakdown_RespectsRangeWindow_NullMeansAllTime()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "recent", 10),
            Entry(DaysAgo(30), "old", 10),
        });
        Assert.Equal(new[] { ("recent", 10) }, History.GetQuestBreakdown(7).Select(r => (r.QuestName, r.Minutes)));
        var all = History.GetQuestBreakdown(null).OrderBy(r => r.QuestName, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("old", 10), ("recent", 10) }, all.Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void GetDailyBreakdown_ReturnsOneEntryPerDay_ZeroFilled()
    {
        History.Save(new List<HistoryEntry> { Entry(DaysAgo(0), "a", 25) });
        var daily = History.GetDailyBreakdown(3);
        Assert.Equal(3, daily.Count);
        Assert.Equal(25, daily[^1].Minutes); // aujourd'hui, en dernier
        Assert.Equal(0, daily[0].Minutes); // il y a 2 jours, rien
    }

    [Fact]
    public void RenameQuest_RelabelsEveryMatchingEntry_MergingWithExistingName()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "Coreen", 20),
            Entry(DaysAgo(1), "Coreen", 10),
            Entry(DaysAgo(1), "Coréen", 15),
        });
        History.RenameQuest("Coreen", "Coréen");
        var breakdown = History.GetQuestBreakdown(null);
        Assert.Equal(new[] { ("Coréen", 45) }, breakdown.Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void RenameQuest_DoesNothing_ForBlankOrUnchangedName()
    {
        History.Save(new List<HistoryEntry> { Entry(DaysAgo(0), "Thèse", 20) });
        History.RenameQuest("Thèse", "  ");
        History.RenameQuest("Thèse", "Thèse");
        Assert.Equal(new[] { ("Thèse", 20) }, History.GetQuestBreakdown(null).Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void RemoveQuest_ReassignsToDefaultBucket_InsteadOfDeleting()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(DaysAgo(0), "Coréen", 20),
            Entry(DaysAgo(0), "", 5),
        });
        History.RemoveQuest("Coréen");
        var breakdown = History.GetQuestBreakdown(null);
        Assert.Equal(new[] { (History.DefaultQuest, 25) }, breakdown.Select(r => (r.QuestName, r.Minutes)));
        Assert.Equal(2, History.Load().Count); // rien n'a ete supprime, juste relabellise
    }

    [Fact]
    public void RemoveQuest_RemovingDefaultBucketItself_IsNoOp()
    {
        History.Save(new List<HistoryEntry> { Entry(DaysAgo(0), "", 20) });
        History.RemoveQuest(History.DefaultQuest);
        Assert.Equal(new[] { (History.DefaultQuest, 20) }, History.GetQuestBreakdown(null).Select(r => (r.QuestName, r.Minutes)));
    }

    [Fact]
    public void GetTimeOfDayBreakdown_BucketsMinutes()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(AtHour(8), "a", 10),  // matin
            Entry(AtHour(14), "a", 20), // apres-midi
            Entry(AtHour(20), "a", 30), // soir
            Entry(AtHour(2), "a", 40),  // nuit
        });
        var breakdown = History.GetTimeOfDayBreakdown(30);
        Assert.Equal(new[] { ("morning", 10), ("afternoon", 20), ("evening", 30), ("night", 40) },
            breakdown.Select(r => (r.Key, r.Minutes)));
    }

    [Fact]
    public void GetTimeOfDayBreakdown_RespectsRangeWindow()
    {
        History.Save(new List<HistoryEntry> { Entry(AtHour(8, 60), "a", 10) });
        var breakdown = History.GetTimeOfDayBreakdown(30);
        Assert.All(breakdown, b => Assert.Equal(0, b.Minutes));
    }

    [Fact]
    public void GetWeekdayBreakdown_ReturnsMinutesPerWeekday_MondayFirst()
    {
        var breakdown = History.GetWeekdayBreakdown(30);
        Assert.Equal(7, breakdown.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 0 }, breakdown.Select(b => b.Dow));
    }

    [Fact]
    public void GetWeekdayBreakdown_CorrectlyAttributesMinutesToTodaysWeekday()
    {
        var now = DateTime.Now;
        History.Save(new List<HistoryEntry> { Entry(new DateTimeOffset(now).ToUnixTimeMilliseconds(), "a", 15) });
        var breakdown = History.GetWeekdayBreakdown(30, now);
        var todayEntry = breakdown.First(b => b.Dow == (int)now.DayOfWeek);
        Assert.Equal(15, todayEntry.Minutes);
    }

    [Fact]
    public void GetCurrentWeekBreakdown_OnlyCountsTheCurrentCalendarWeek_NotOlderWeeks()
    {
        // Régression : le rond du panneau "streak" utilisait GetWeekdayBreakdown
        // (agrégat sur 30 jours), donc une seule session un lundi d'il y a 3
        // semaines suffisait à allumer TOUS les jours de la semaine affichée,
        // même ceux non faits cette semaine-ci - trompeur juste à côté d'un
        // streak de 1 jour ("Streak.md" screenshot signalé par l'utilisateur).
        var now = new DateTime(2026, 1, 19, 12, 0, 0); // lundi 19 janvier 2026
        var threeWeeksAgoMonday = now.AddDays(-21); // lundi 29 décembre 2025 - même jour de semaine, semaine différente
        History.Save(new List<HistoryEntry> { Entry(new DateTimeOffset(threeWeeksAgoMonday).ToUnixTimeMilliseconds(), "a", 30) });

        var thisWeek = History.GetCurrentWeekBreakdown(now);
        Assert.All(thisWeek, row => Assert.Equal(0, row.Minutes));

        var last30Days = History.GetWeekdayBreakdown(30, now);
        Assert.Contains(last30Days, row => row.Dow == (int)threeWeeksAgoMonday.DayOfWeek && row.Minutes > 0);
    }

    [Fact]
    public void GetCurrentWeekBreakdown_AttributesMinutesToTheCorrectDayInThisWeek()
    {
        var monday = new DateTime(2026, 1, 19, 9, 0, 0); // lundi 19 janvier 2026
        var wednesday = monday.AddDays(2);
        History.Save(new List<HistoryEntry> { Entry(new DateTimeOffset(wednesday).ToUnixTimeMilliseconds(), "a", 25) });

        var week = History.GetCurrentWeekBreakdown(monday);
        var wedRow = week.First(r => r.Dow == (int)wednesday.DayOfWeek);
        Assert.Equal(25, wedRow.Minutes);
        Assert.All(week.Where(r => r.Dow != (int)wednesday.DayOfWeek), r => Assert.Equal(0, r.Minutes));
    }

    [Fact]
    public void GetSuggestedStartHour_UsesRecentSessionStartTimes()
    {
        History.Save(new List<HistoryEntry>
        {
            Entry(AtHour(10), "a", 60),
            Entry(AtHour(10, 1), "a", 60),
            Entry(AtHour(16, 2), "a", 30),
        });

        Assert.Equal(9, History.GetSuggestedStartHour());
    }
}
