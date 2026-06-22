using System.Security.Cryptography;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Generates human-readable passphrases for bulk user creation: 3–4 random words, each Title-cased,
/// joined by a random per-gap separator from <c>-</c> / <c>_</c>, padded to at least
/// <see cref="MinLength"/> characters. Mixed case plus the symbol separator gives three of the four AD
/// complexity categories (upper, lower, symbol), so no digit is needed to satisfy default domain
/// complexity. Uses <see cref="RandomNumberGenerator"/> for all randomness.
/// </summary>
public static class PassphraseGenerator
{
    private const int MinLength = 12;
    private static readonly char[] Separators = { '-', '_' };

    /// <summary>Returns a fresh passphrase, e.g. <c>Brave-Tiger_Maple-River</c>.</summary>
    public static string Generate()
    {
        // Build 3 words, then keep appending words until we clear the minimum length (still readable).
        var words = new List<string>(5);
        for (var i = 0; i < 3; i++) words.Add(Pick(Words));

        string Join() => Assemble(words);
        while (Join().Length < MinLength && words.Count < 6)
            words.Add(Pick(Words));

        return Join();
    }

    /// <summary>Title-cases each word and stitches them with a random separator chosen independently per gap.</summary>
    private static string Assemble(IReadOnlyList<string> words)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(Pick(Separators));
            sb.Append(TitleCase(words[i]));
        }
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
