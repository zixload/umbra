using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Umbra.Core;

public class HistoryEntry
{
    [JsonPropertyName("endedAt")]
    public long EndedAt { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "custom";

    [JsonPropertyName("hardMode")]
    public bool HardMode { get; set; }

    [JsonPropertyName("questName")]
    public string QuestName { get; set; } = "";

    [JsonPropertyName("focusedMinutes")]
    public double FocusedMinutes { get; set; }
}

public class HistoryStats
{
    public int TodayMinutes { get; set; }
    public int WeekMinutes { get; set; }
    public int MonthMinutes { get; set; }
    public int AverageSessionMinutes { get; set; }
    public int StreakDays { get; set; }
    public int BestStreakDays { get; set; }
    public int TotalSessions { get; set; }
    public int TotalMinutes { get; set; }
    public int BestDayMinutes { get; set; }
    public int LongestSessionMinutes { get; set; }
    // Semaine calendaire précédente (lundi -> lundi), pour afficher une
    // tendance ("+18%") à côté de WeekMinutes - 0 si aucune session n'a été
    // faite cette semaine-là (la comparaison n'a alors pas de sens, l'appelant
    // doit éviter le pourcentage plutôt que diviser par zéro).
    public int PreviousWeekMinutes { get; set; }
}

public record QuestBreakdownRow(string QuestName, int Minutes);
public record DailyBreakdownRow(string Date, int Minutes);
public record TimeOfDayRow(string Key, int Minutes);
public record WeekdayRow(int Dow, int Minutes);

public static class History
{
    public const string DefaultQuest = "Session de focus";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Session.Stop() est appelé aussi bien depuis le GUI (clic Stop) que
    // depuis le watchdog, process séparé (fin naturelle d'une session
    // custom) - si les deux se déclenchent à quelques centaines de ms
    // d'écart (l'utilisateur clique Stop pile quand le temps arrive à 0),
    // chacun charge sa propre copie de history.json avant que l'autre
    // n'écrive, et les deux Append() finissent par compter la même session
    // deux fois. Un Mutex nommé (visible entre process, contrairement à un
    // lock C# classique) sérialise les lectures-modifications-écritures.
    private static readonly Mutex FileMutex = new(false, "UmbraNative_HistoryFileMutex");

    public static List<HistoryEntry> Load()
    {
        if (!File.Exists(Config.HistoryFile)) return new List<HistoryEntry>();
        try
        {
            var json = File.ReadAllText(Config.HistoryFile);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
        }
        catch
        {
            return new List<HistoryEntry>();
        }
    }

    public static void Save(List<HistoryEntry> entries)
    {
        AtomicFile.WriteAllText(Config.HistoryFile, JsonSerializer.Serialize(entries, JsonOptions));
    }

    // Efface tout l'historique de sessions (bouton "Effacer l'historique" des
    // réglages) - remet simplement le fichier à une liste vide, mêmes
    // garanties que Save() côté écriture atomique du fichier.
    public static void Clear() => Save(new List<HistoryEntry>());

    // Ajoute une entrée quand une session (custom ou pomodoro) se termine,
    // qu'elle soit allée à son terme ou arrêtée en cours de route.
    public static void Append(string kind, bool hardMode, string questName, double focusedMinutes, long? endedAt = null)
    {
        if (focusedMinutes < 0.1) return; // rien à enregistrer, session quasi instantanée
        FileMutex.WaitOne();
        try
        {
            var entries = Load();
            entries.Add(new HistoryEntry
            {
                EndedAt = endedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = kind,
                HardMode = hardMode,
                QuestName = questName,
                FocusedMinutes = Math.Round(focusedMinutes * 10) / 10,
            });
            // garde un historique raisonnable, pas la peine de grossir indéfiniment
            var trimmed = entries.Count > 2000 ? entries.GetRange(entries.Count - 2000, 2000) : entries;
            Save(trimmed);
        }
        finally
        {
            FileMutex.ReleaseMutex();
        }
    }

    private static DateTime ToLocal(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().DateTime;

    private static string DayKey(long unixMs)
    {
        var d = ToLocal(unixMs);
        return $"{d.Year}-{d.Month}-{d.Day}";
    }

    public static HistoryStats GetStats(DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var todayKey = DayKey(new DateTimeOffset(now).ToUnixTimeMilliseconds());

        double todayMinutes = 0;
        var minutesByDay = new Dictionary<string, double>();
        foreach (var e in entries)
        {
            var key = DayKey(e.EndedAt);
            minutesByDay[key] = minutesByDay.GetValueOrDefault(key) + e.FocusedMinutes;
            if (key == todayKey) todayMinutes += e.FocusedMinutes;
        }

        // série : nombre de jours consécutifs (en remontant depuis
        // aujourd'hui ou hier si rien fait encore aujourd'hui) avec au
        // moins une session terminée.
        var streak = 0;
        var cursor = now.Date;
        if (!minutesByDay.ContainsKey(DayKey(new DateTimeOffset(cursor).ToUnixTimeMilliseconds())))
        {
            cursor = cursor.AddDays(-1);
        }
        while (minutesByDay.ContainsKey(DayKey(new DateTimeOffset(cursor).ToUnixTimeMilliseconds())))
        {
            streak += 1;
            cursor = cursor.AddDays(-1);
        }

        // record : la plus longue série jamais atteinte, pas juste celle en
        // cours - pour chaque jour qui démarre une série (son veille n'a
        // aucune session), on avance tant que les jours suivants en ont une.
        var activeDays = new HashSet<DateTime>(entries.Select(e => ToLocal(e.EndedAt).Date));
        var bestStreak = 0;
        foreach (var day in activeDays)
        {
            if (activeDays.Contains(day.AddDays(-1))) continue;
            var length = 0;
            var runCursor = day;
            while (activeDays.Contains(runCursor))
            {
                length++;
                runCursor = runCursor.AddDays(1);
            }
            bestStreak = Math.Max(bestStreak, length);
        }

        // semaine en cours (lundi -> aujourd'hui), et la semaine calendaire
        // précédente (lundi -> lundi) pour la tendance affichée à côté.
        var dow = ((int)now.DayOfWeek + 6) % 7; // 0 = lundi
        var weekStart = now.Date.AddDays(-dow);
        var weekStartMs = new DateTimeOffset(weekStart).ToUnixTimeMilliseconds();
        var previousWeekStartMs = new DateTimeOffset(weekStart.AddDays(-7)).ToUnixTimeMilliseconds();
        double weekMinutes = 0;
        double previousWeekMinutes = 0;
        foreach (var e in entries)
        {
            if (e.EndedAt >= weekStartMs) weekMinutes += e.FocusedMinutes;
            else if (e.EndedAt >= previousWeekStartMs) previousWeekMinutes += e.FocusedMinutes;
        }

        // mois civil en cours
        double monthMinutes = 0;
        foreach (var e in entries)
        {
            var d = ToLocal(e.EndedAt);
            if (d.Year == now.Year && d.Month == now.Month) monthMinutes += e.FocusedMinutes;
        }

        // moyenne par session sur les 30 derniers jours (plus représentatif
        // du rythme actuel qu'une moyenne all-time qui se tasse avec le temps)
        var avgSinceMs = new DateTimeOffset(now).ToUnixTimeMilliseconds() - 30L * 24 * 3600 * 1000;
        var recent = entries.Where(e => e.EndedAt >= avgSinceMs).ToList();
        var averageSessionMinutes = recent.Count > 0 ? (int)Math.Round(recent.Average(e => e.FocusedMinutes)) : 0;

        return new HistoryStats
        {
            TodayMinutes = (int)Math.Round(todayMinutes),
            WeekMinutes = (int)Math.Round(weekMinutes),
            MonthMinutes = (int)Math.Round(monthMinutes),
            AverageSessionMinutes = averageSessionMinutes,
            StreakDays = streak,
            BestStreakDays = Math.Max(bestStreak, streak),
            TotalSessions = entries.Count,
            TotalMinutes = (int)Math.Round(entries.Sum(e => e.FocusedMinutes)),
            BestDayMinutes = minutesByDay.Count > 0 ? (int)Math.Round(minutesByDay.Values.Max()) : 0,
            LongestSessionMinutes = entries.Count > 0 ? (int)Math.Round(entries.Max(e => e.FocusedMinutes)) : 0,
            PreviousWeekMinutes = (int)Math.Round(previousWeekMinutes),
        };
    }

    // Répartition du temps par quête ("Thèse", "Coréen"...) sur une fenêtre
    // donnée - réutilise le champ QuestName déjà stocké par session, sans
    // imposer un système de tags séparé à apprendre. rangeDays=null => tout
    // l'historique.
    public static List<QuestBreakdownRow> GetQuestBreakdown(int? rangeDays = 7, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var sinceMs = rangeDays == null ? 0 : new DateTimeOffset(now).ToUnixTimeMilliseconds() - rangeDays.Value * 24L * 3600 * 1000;
        var byQuest = new Dictionary<string, double>();
        foreach (var e in entries)
        {
            if (e.EndedAt < sinceMs) continue;
            var name = string.IsNullOrWhiteSpace(e.QuestName) ? DefaultQuest : e.QuestName.Trim();
            byQuest[name] = byQuest.GetValueOrDefault(name) + e.FocusedMinutes;
        }
        return byQuest
            .Select(kv => new QuestBreakdownRow(kv.Key, (int)Math.Round(kv.Value)))
            .OrderByDescending(r => r.Minutes)
            .ToList();
    }

    // Minutes par jour sur les N derniers jours (inclut les jours à 0, pour
    // un visuel jour par jour régulier plutôt qu'une liste creuse).
    public static List<DailyBreakdownRow> GetDailyBreakdown(int days = 14, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var minutesByDay = new Dictionary<string, double>();
        foreach (var e in entries)
        {
            var key = DayKey(e.EndedAt);
            minutesByDay[key] = minutesByDay.GetValueOrDefault(key) + e.FocusedMinutes;
        }

        var result = new List<DailyBreakdownRow>();
        var cursor = now.Date;
        for (var i = days - 1; i >= 0; i--)
        {
            var d = cursor.AddDays(-i);
            var key = DayKey(new DateTimeOffset(d).ToUnixTimeMilliseconds());
            result.Add(new DailyBreakdownRow(
                $"{d.Year}-{d.Month:D2}-{d.Day:D2}",
                (int)Math.Round(minutesByDay.GetValueOrDefault(key))));
        }
        return result;
    }

    private static string HourBucket(int hour)
    {
        if (hour >= 5 && hour < 12) return "morning";
        if (hour >= 12 && hour < 18) return "afternoon";
        if (hour >= 18 && hour < 22) return "evening";
        return "night";
    }

    // Répartition du temps par moment de la journée (matin/après-midi/soir/
    // nuit), basée sur l'heure de fin de session - une approximation
    // suffisante pour repérer une tendance ("plutôt du soir"), sans avoir à
    // stocker une heure de début séparée juste pour ça.
    public static List<TimeOfDayRow> GetTimeOfDayBreakdown(int? rangeDays = 30, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var sinceMs = rangeDays == null ? 0 : new DateTimeOffset(now).ToUnixTimeMilliseconds() - rangeDays.Value * 24L * 3600 * 1000;
        var buckets = new Dictionary<string, double> { ["morning"] = 0, ["afternoon"] = 0, ["evening"] = 0, ["night"] = 0 };
        foreach (var e in entries)
        {
            if (e.EndedAt < sinceMs) continue;
            buckets[HourBucket(ToLocal(e.EndedAt).Hour)] += e.FocusedMinutes;
        }
        return new[] { "morning", "afternoon", "evening", "night" }
            .Select(key => new TimeOfDayRow(key, (int)Math.Round(buckets[key])))
            .ToList();
    }

    // Répartition du temps par jour de la semaine (lundi -> dimanche), pour
    // repérer le jour où tu bosses le plus sur la période.
    public static List<WeekdayRow> GetWeekdayBreakdown(int? rangeDays = 30, DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var sinceMs = rangeDays == null ? 0 : new DateTimeOffset(now).ToUnixTimeMilliseconds() - rangeDays.Value * 24L * 3600 * 1000;
        var minutesByDow = new double[7]; // index = DayOfWeek (0 = dimanche)
        foreach (var e in entries)
        {
            if (e.EndedAt < sinceMs) continue;
            minutesByDow[(int)ToLocal(e.EndedAt).DayOfWeek] += e.FocusedMinutes;
        }
        var mondayFirst = new[] { 1, 2, 3, 4, 5, 6, 0 };
        return mondayFirst.Select(dow => new WeekdayRow(dow, (int)Math.Round(minutesByDow[dow]))).ToList();
    }

    // Contrairement à GetWeekdayBreakdown (agrège par jour de semaine sur 30
    // jours - "le lundi, tu bosses en moyenne combien" - donc un seul lundi
    // travaillé sur le mois entier suffit à allumer le rond "L" même les
    // semaines où rien n'a été fait), ceci reflète UNIQUEMENT la semaine
    // civile en cours (lundi -> dimanche) : c'est ce qu'il faut à côté d'un
    // "streak" pour ne pas donner l'impression trompeuse que toute la
    // semaine est déjà faite.
    public static List<WeekdayRow> GetCurrentWeekBreakdown(DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var entries = Load();
        var dow = ((int)now.DayOfWeek + 6) % 7; // 0 = lundi
        var weekStart = now.Date.AddDays(-dow);
        var minutesByDay = new Dictionary<string, double>();
        foreach (var e in entries)
        {
            var key = DayKey(e.EndedAt);
            minutesByDay[key] = minutesByDay.GetValueOrDefault(key) + e.FocusedMinutes;
        }
        var result = new List<WeekdayRow>();
        for (var i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            var key = DayKey(new DateTimeOffset(day).ToUnixTimeMilliseconds());
            result.Add(new WeekdayRow((int)day.DayOfWeek, (int)Math.Round(minutesByDay.GetValueOrDefault(key))));
        }
        return result;
    }

    public static int? GetSuggestedStartHour(DateTime? nowOpt = null)
    {
        var now = nowOpt ?? DateTime.Now;
        var since = new DateTimeOffset(now).ToUnixTimeMilliseconds() - 30L * 24 * 3600 * 1000;
        var recent = Load().Where(entry => entry.EndedAt >= since).ToList();
        if (recent.Count == 0) return null;
        return recent
            .Select(entry => ToLocal(entry.EndedAt).AddMinutes(-entry.FocusedMinutes).Hour)
            .GroupBy(hour => hour)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;
    }

    // Renomme une quête dans tout l'historique (fusionne les minutes si le
    // nouveau nom existe déjà, puisque GetQuestBreakdown agrège par nom).
    public static void RenameQuest(string oldName, string? newName)
    {
        var trimmed = (newName ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == oldName) return;
        FileMutex.WaitOne();
        try
        {
            var entries = Load();
            var changed = false;
            foreach (var e in entries)
            {
                var current = string.IsNullOrWhiteSpace(e.QuestName) ? DefaultQuest : e.QuestName.Trim();
                if (current == oldName)
                {
                    e.QuestName = trimmed;
                    changed = true;
                }
            }
            if (changed) Save(entries);
        }
        finally
        {
            FileMutex.ReleaseMutex();
        }
    }

    // "Retire" une quête de la répartition : les entrées concernées
    // retombent dans la catégorie par défaut plutôt que d'être supprimées -
    // le temps de focus déjà enregistré ne disparaît jamais, seule
    // l'étiquette change.
    public static void RemoveQuest(string name)
    {
        if (name == DefaultQuest) return;
        RenameQuest(name, DefaultQuest);
    }
}
