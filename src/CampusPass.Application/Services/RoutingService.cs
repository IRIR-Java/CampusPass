using CampusPass.Application.Abstractions;
using CampusPass.Domain.Entities;

namespace CampusPass.Application.Services;

public record RoutingResult(Sector Sector, double Confidence, IReadOnlyList<string> MatchedKeywords);

/// <summary>
/// Intelligent routing v1: weighted keyword scoring. Deliberately simple and explainable.
/// Anything we cannot classify goes to the default (Help Desk) sector for manual triage.
/// The Route() signature is the seam where an ML / LLM classifier can replace this later.
/// </summary>
public class RoutingService(ISectorRepository sectors)
{
    public RoutingResult Route(string title, string description)
    {
        var text = $"{title} {description}".ToLowerInvariant();
        var all = sectors.All();

        var scored = all
            .Select(s =>
            {
                var hits = s.Keywords.Where(k => text.Contains(k.Key.ToLowerInvariant())).ToList();
                return (Sector: s, Score: hits.Sum(h => h.Value), Hits: hits.Select(h => h.Key).ToList());
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
        {
            var fallback = all.FirstOrDefault(s => s.IsDefault) ?? all.First();
            return new RoutingResult(fallback, 0, Array.Empty<string>());
        }

        var total = scored.Sum(x => x.Score);
        var best = scored[0];
        return new RoutingResult(best.Sector, Math.Round((double)best.Score / total, 2), best.Hits);
    }
}
