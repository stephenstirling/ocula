using System.Collections.Generic;

namespace Ocula.Services;

/// <summary>
/// The things Ocula can suggest as tags. For each label, CLIP is asked how well "a photo of
/// {label}" matches the picture; the best few above a confidence threshold become suggestions.
/// Short, common, visual words work best. Add or remove entries freely: label embeddings
/// are rebuilt automatically when this list changes.
/// </summary>
public static class SmartLabels
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        // Scenes and places
        "beach", "ocean", "lake", "river", "waterfall", "mountain", "forest", "desert", "snow", "field",
        "countryside", "city street", "skyline", "bridge", "castle", "church", "temple", "museum", "market",
        "restaurant", "kitchen", "bedroom", "living room", "office", "garden", "park", "playground", "stadium",
        "airport", "train station", "harbour", "road", "highway", "island", "cave", "canyon", "volcano",
        "farm", "vineyard", "street at night", "rooftop", "swimming pool", "campsite",
        // Sky and light
        "sunset", "sunrise", "golden hour", "blue hour", "night sky", "stars", "milky way", "clouds", "storm",
        "rainbow", "fog", "rain", "lightning", "moon", "silhouette", "reflection", "city lights", "fireworks",
        // Nature and animals
        "flower", "tree", "autumn leaves", "grass", "cactus", "mushroom", "dog", "cat", "horse", "cow", "sheep",
        "goat", "bird", "owl", "eagle", "duck", "fish", "butterfly", "insect", "bee", "squirrel", "deer", "bear",
        "lion", "elephant", "monkey", "turtle", "whale", "dolphin",
        // People and events
        "portrait", "group photo", "selfie", "baby", "child", "couple", "family", "crowd", "wedding", "birthday party",
        "graduation", "concert", "festival", "parade", "protest", "speech",
        // Sports and activities (CLIP can tell these apart well; add your own)
        "sports", "volleyball", "beach volleyball", "soccer", "american football", "basketball", "baseball", "softball",
        "tennis", "badminton", "table tennis", "golf", "hockey", "ice skating", "rugby", "cricket", "boxing",
        "martial arts", "wrestling", "gymnastics", "cheerleading", "track and field", "running", "cycling",
        "mountain biking", "skiing", "snowboarding", "skateboarding", "surfing", "paddleboarding", "kayaking",
        "rowing", "sailing", "swimming", "scuba diving", "fishing", "rock climbing", "hiking", "horse riding",
        "archery", "bowling", "weightlifting", "yoga", "dancing", "motor racing", "camping", "picnic", "barbecue",
        // Clothing and accessories (everyday terms only)
        "dress", "suit", "jacket", "coat", "uniform", "hat", "sunglasses", "scarf", "boots", "sneakers",
        "wetsuit", "swimwear", "swimsuit", "backpack", "jewellery", "costume",
        // Things
        "car", "motorcycle", "bicycle", "bus", "train", "airplane", "boat", "sailboat", "truck", "taxi",
        "building", "house", "apartment building", "skyscraper", "window", "door", "stairs", "statue",
        "food", "pizza", "burger", "sushi", "salad", "cake", "dessert", "coffee", "wine", "beer", "breakfast",
        "fruit", "vegetables", "book", "laptop", "phone", "television", "guitar", "piano", "painting",
        "toy", "balloon", "christmas tree", "pumpkin", "candles", "gift", "flag", "sign", "text", "document",
        "screenshot", "map", "chart", "drawing", "poster", "receipt", "whiteboard",
        // Style
        "candid", "posed portrait", "street photography", "black and white photo", "macro", "aerial view", "panorama", "long exposure", "abstract", "minimalist",
    };

    /// <summary>
    /// Labels that mean nearly the same thing in a photo. CLIP spreads its confidence across them,
    /// so a clear portrait could come out as two weak guesses ("portrait" 5%, "posed portrait" 4%)
    /// and get no tag at all. A group's labels share their confidence and only the best one is
    /// suggested. Every label here must also be in <see cref="All"/>, and in one group at most.
    /// </summary>
    public static readonly IReadOnlyList<string[]> Groups = new[]
    {
        new[] { "portrait", "posed portrait" },
        new[] { "volleyball", "beach volleyball" },
        new[] { "baseball", "softball" },
        new[] { "running", "track and field" },
        new[] { "boat", "sailboat", "sailing" },
        new[] { "camping", "campsite" },
        new[] { "sunset", "sunrise", "golden hour" },
        new[] { "night sky", "stars", "milky way" },
        new[] { "building", "apartment building" },
        new[] { "jacket", "coat" },
        new[] { "swimwear", "swimsuit" },
        new[] { "cake", "dessert" },
    };
}
