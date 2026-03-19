namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface ISessionFilter
{
    string GetCurrentSession(DateTime utcTime);
    bool IsSessionAllowed(string session, IReadOnlyList<string> allowedSessions);
}
