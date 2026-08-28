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

    /// <summary>
    /// Graph's machine-readable error code (<c>Request_BadRequest</c>, <c>Request_ResourceNotFound</c>, …), or
    /// null for anything that isn't an <see cref="ODataError"/>. Callers that branch on a failure should prefer
    /// this over <see cref="Friendly"/>: the message is prose Microsoft can reword, the code is contract.
    /// </summary>
    public static string? Code(Exception ex) => ex is ODataError oe ? oe.Error?.Code : null;
}
