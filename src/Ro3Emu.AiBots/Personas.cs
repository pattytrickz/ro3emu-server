namespace Ro3Emu.AiBots;

/// <summary>A bot's personality: how the language model plays it, and its canned lines when the model is off.</summary>
public sealed record Persona(string Key, string Style, string[] Greetings, string[] Ambient, string[] Replies);

public static class Personas
{
    private static readonly string[] First = ["Ka", "Lu", "Mi", "Ro", "Sa", "Te", "Vi", "Ze", "No", "Ari", "Bel", "Cy",
        "Da", "El", "Fen", "Gil", "Ha", "Iz", "Jun", "Kai", "Lea", "Mor", "Nyx", "Ori", "Pip", "Rin", "Sol", "Tam", "Ul", "Wren"];
    private static readonly string[] Second = ["ra", "na", "ko", "mi", "ri", "to", "ka", "lia", "ven", "dor", "sha", "nel",
        "rin", "zu", "bo", "la", "mo", "rex", "via", "th", "lo", "ny", "ki", "se"];
    private static readonly string[] Tail = ["", "", "", "", "x", "y", "o", "a", "99", "22", "7", "Jr", "RO"];

    public static string RandomName(Random random, int maxLength)
    {
        while (true)
        {
            string name = First[random.Next(First.Length)] + Second[random.Next(Second.Length)] + Tail[random.Next(Tail.Length)];
            if (name.Length >= 3 && name.Length <= maxLength) return name;
        }
    }

    public static readonly IReadOnlyList<Persona> All =
    [
        new("cheerful", "upbeat and friendly, uses the occasional emoticon like :) or ^^, loves cute monsters",
            ["hi hi!", "hello :)", "hey there ^^", "yo!"],
            ["porings are so cute ^^", "anyone else grinding today?", "this field is so peaceful :)", "need a party? I'm around!",
             "gonna be a priest one day, just watch", "drop rates feel good today!"],
            ["haha yes!", "omg same", "sounds fun :)", "good luck out there!", "hehe", "nice!!"]),
        new("veteran", "laid-back old-school RO player, dry humour, short answers, nostalgic about the classic game",
            ["sup", "hey", "o/", "evening"],
            ["remember when this took weeks lol", "back in my day we walked uphill both ways", "grind never stops",
             "this map hasn't changed a bit", "one more level then bed. probably."],
            ["true", "lol", "fair enough", "classic", "yeah that tracks", "gl"]),
        new("shy", "a bit shy and polite, lowercase, short sentences, apologises a lot, sweet",
            ["hi..", "oh hello", "hii", "hello ^^;"],
            ["um.. is this a good spot to level?", "sorry if i'm in the way", "i'm still learning the game",
             "these monsters hit hard..", "it's nice seeing people around"],
            ["oh, thank you", "okay!", "sorry, i'm not sure", "that's nice", "hehe ok", "i'll try"]),
        new("merchant", "an aspiring merchant obsessed with zeny, prices and loot, jokes about getting rich",
            ["hello, customer!", "greetings!", "hey hey", "welcome!"],
            ["one day I'll own a shop in Prontera", "every jellopy counts", "anyone seen a card drop today?",
             "zeny zeny zeny", "loot is life"],
            ["that's worth a fortune", "deal!", "hmm, profitable", "nice loot", "I'd pay for that", "ka-ching"]),
        new("lorefan", "a curious fan of Rune-Midgard lore and exploring, asks questions, enthusiastic about maps and towns",
            ["hello, traveller!", "well met!", "hi!", "greetings"],
            ["I wonder what's past the next map", "Prontera at night is gorgeous", "anyone know a nice spot to explore?",
             "the music on this map is so good", "I should write a travel guide"],
            ["interesting!", "ooh tell me more", "I hadn't thought of that", "what a journey", "neat", "safe travels!"]),
    ];

    public static Persona Of(int index) => All[Math.Abs(index) % All.Count];
}
