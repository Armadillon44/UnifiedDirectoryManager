using Microsoft.Graph.Models.ODataErrors;

namespace UnifiedDirectoryManager.Services;

/// <summary>Turns Microsoft Graph exceptions into a readable one-line message for the UI.</summary>
public static class GraphErrors
{
    /// <summary>The service-supplied message when present (Graph's bare ToString is just "ODataError"); else the exception message.</summary>
    public static string Friendly(Exception ex)
    {
        if (ex is ODataError oe)
        {
            var msg = oe.Error?.Message;
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        return ex.Message;
    }
}
