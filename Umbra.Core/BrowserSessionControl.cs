namespace Umbra.Core;

public record BrowserSessionStatus(
    bool Active,
    string Kind,
    string? Phase,
    long EndTs,
    double RemainingSeconds,
    bool CanStop,
    bool HardMode,
    string QuestName,
    int Cycle,
    int CyclesTotal);

public static class BrowserSessionControl
{
    public static BrowserSessionStatus GetStatus() => CreateStatus(Session.Load());

    public static bool TryStop(out BrowserSessionStatus status, out string? error)
    {
        var session = Session.Load();
        if (session.Active && !Session.CanStop(session))
        {
            status = CreateStatus(session);
            error = "hardMode";
            return false;
        }

        if (session.Active) Session.Stop(session);
        status = CreateStatus(session);
        error = null;
        return true;
    }

    private static BrowserSessionStatus CreateStatus(SessionState session)
    {
        var pomodoro = session.Pomodoro;
        return new BrowserSessionStatus(
            session.Active,
            session.Kind,
            session.Kind == "pomodoro" ? pomodoro?.Phase : null,
            session.EndTs,
            Session.RemainingSeconds(session),
            Session.CanStop(session),
            session.HardMode,
            session.QuestName,
            pomodoro is null ? 0 : pomodoro.CycleIndex + 1,
            pomodoro?.CyclesTotal ?? 0);
    }
}
