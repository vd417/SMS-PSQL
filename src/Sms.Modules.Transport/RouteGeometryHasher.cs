using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sms.Modules.Transport;

public static class RouteGeometryHasher
{
    /// Deterministic hash over the planned-route inputs: the (optional) school origin
    /// followed by the ordered stop list. Any add/remove/reorder/coordinate change to
    /// `RouteStops` — OR a move of the school origin — changes this value, which is how
    /// RouteGeometryService decides whether cached geometry can still be reused.
    public static string Compute(
        IReadOnlyList<RouteStopListItem> orderedStops, (double Lat, double Lng)? origin = null)
    {
        var sb = new StringBuilder();
        if (origin is { } o)
            sb.Append("O|")
              .Append(o.Lat.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(o.Lng.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        foreach (var s in orderedStops)
        {
            sb.Append(s.Id).Append('|')
              .Append(s.Sequence).Append('|')
              .Append(s.Lat.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(s.Lng.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
