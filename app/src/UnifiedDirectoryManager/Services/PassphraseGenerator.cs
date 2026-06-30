using System.Security.Cryptography;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// The single password generator shared by every credential-issuing flow (New User, Copy User, Bulk
/// Create, Reset Password). Produces 3 random Title-cased words joined by a random per-gap separator
/// (<c>-</c> / <c>_</c>), then a final separator followed by 5 random unambiguous alphanumeric characters
/// — e.g. <c>Brave-Tiger_Maple-7kR2m</c>. Title-cased words plus a symbol separator already cover three of
/// the four AD complexity categories (upper, lower, symbol); the suffix adds entropy (and usually a digit).
/// Uses <see cref="RandomNumberGenerator"/> for all randomness.
/// </summary>
public static class PassphraseGenerator
{
    private const int WordCount = 3;
    private const int SuffixLength = 5;
    private static readonly char[] Separators = { '-', '_' };

    // Unambiguous suffix alphabet: lower (no l/o), upper (no I/O), digits (no 0/1) — same exclusions used elsewhere.
    private const string SuffixChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Returns a fresh password, e.g. <c>Brave-Tiger_Maple-7kR2m</c>.</summary>
    public static string Generate()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < WordCount; i++)
        {
            if (i > 0) sb.Append(Pick(Separators));
            sb.Append(TitleCase(Pick(Words)));
        }
        sb.Append(Pick(Separators)); // separator before the random suffix
        for (var i = 0; i < SuffixLength; i++)
            sb.Append(SuffixChars[RandomNumberGenerator.GetInt32(SuffixChars.Length)]);
        return sb.ToString();
    }

    private static string TitleCase(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static T Pick<T>(IReadOnlyList<T> set) => set[RandomNumberGenerator.GetInt32(set.Count)];

    /// <summary>
    /// Curated word list: short, common, unambiguous words (no profanity, no easily-confused homophones).
    /// Kept lower-case here; <see cref="TitleCase"/> applies casing at assembly time.
    /// </summary>
    private static readonly string[] Words =
    {
        "able", "acid", "acorn", "alert", "amber", "anchor", "angle", "apple", "april", "arch",
        "arrow", "aspen", "atlas", "aurora", "autumn", "azure", "badge", "baker", "bamboo", "banjo",
        "basil", "beacon", "beam", "bear", "beetle", "birch", "bison", "blaze", "bloom", "bluff",
        "bolt", "bonus", "boron", "brave", "bread", "brick", "bridge", "bright", "bronze", "brook",
        "brush", "bubble", "buffalo", "bunny", "cabin", "cable", "cactus", "camel", "candle", "canoe",
        "canyon", "carbon", "cargo", "carrot", "castle", "cedar", "chalk", "charm", "cherry", "chess",
        "chili", "cider", "cinder", "citrus", "clay", "clever", "cliff", "cloud", "clover", "cobalt",
        "comet", "compass", "copper", "coral", "cosmos", "cotton", "cougar", "coyote", "crane", "crater",
        "cricket", "crimson", "crisp", "crow", "crystal", "cyan", "daisy", "dawn", "delta", "denim",
        "desert", "diamond", "dolphin", "domino", "dragon", "dream", "drift", "dune", "eagle", "ember",
        "emerald", "engine", "ester", "ether", "fable", "falcon", "feather", "fennel", "fern", "ferry",
        "fiber", "field", "finch", "flame", "flint", "flora", "flute", "forest", "fox", "frost",
        "galaxy", "garden", "garnet", "gecko", "ginger", "glacier", "glass", "globe", "gold", "granite",
        "grape", "grove", "guava", "harbor", "harvest", "hawk", "hazel", "heron", "hickory", "honey",
        "horizon", "ivory", "jade", "jasmine", "jolly", "jungle", "juniper", "kayak", "kelp", "kettle",
        "lagoon", "lantern", "lark", "laurel", "lemon", "lily", "linen", "lotus", "lunar", "lynx",
        "magnet", "mango", "maple", "marble", "marsh", "meadow", "melon", "mint", "misty", "moon",
        "moss", "mountain", "nectar", "nimble", "noble", "north", "nova", "oak", "ocean", "olive",
        "onyx", "opal", "orange", "orbit", "orchid", "otter", "owl", "oxide", "palm", "panda",
        "papaya", "pearl", "pebble", "pepper", "pewter", "pilot", "pine", "pixel", "planet", "plaza",
        "plum", "pond", "poppy", "prairie", "puma", "quartz", "quick", "quill", "quiver", "rabbit",
        "radar", "radish", "rapid", "raven", "reef", "ridge", "river", "robin", "rocket", "rose",
        "ruby", "saddle", "saffron", "sage", "salmon", "sandy", "sapling", "sequoia", "shadow", "shell",
        "silver", "sky", "slate", "snow", "solar", "sonic", "sparrow", "spice", "spring", "spruce",
        "stone", "storm", "stream", "summit", "sunny", "swift", "sycamore", "tango", "tiger", "timber",
        "topaz", "torch", "trail", "tulip", "tundra", "turtle", "twig", "umber", "valley", "velvet",
        "vine", "violet", "vivid", "walnut", "willow", "winter", "wolf", "wren", "yarrow", "zephyr",
    };
}
