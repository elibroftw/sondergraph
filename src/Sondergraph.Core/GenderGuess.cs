namespace Sondergraph.Core;

/// <summary>
/// A guess at a person's gender from their first name.
/// </summary>
/// <param name="Gender">The guessed value, e.g. <c>M</c> or <c>F</c>.</param>
/// <param name="Source">The first name the guess was based on.</param>
/// <param name="Confidence">How reliable the mapping is, 0..1.</param>
public sealed record GenderPrediction(string Gender, string Source, double Confidence);

/// <summary>
/// Infers a gender from a first name, for the user to accept or reject.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a guess, and it is stored as one.</b> It was built on the explicit request to
/// fill in genders from names, and the value it produces is written into the same field a
/// human edit would use - so the record of <em>how</em> it was arrived at matters. Callers
/// are expected to mark inferred values in <see cref="Person.GenderSource"/> so a wrong batch
/// can be found and reverted.
/// </para>
/// <para>
/// <b>Ambiguous names get no guess.</b> Alex, Sam, Jordan, Casey, Jamie and the rest return
/// null rather than the more common gender. A blank is visibly unfilled and prompts the user
/// to look; a confident wrong answer on a real person does not. For a guest list, the second
/// failure is the expensive one.
/// </para>
/// <para>
/// The table covers names that are common in English-speaking countries and reasonably
/// unambiguous. It does not attempt worldwide coverage, and a name it does not know returns
/// null rather than defaulting to anything.
/// </para>
/// </remarks>
public static class GenderGuess
{
    /// <summary>
    /// Names that are used by more than one gender. No guess is offered for these.
    /// </summary>
    /// <remarks>
    /// This list is the most important part of the file. Every addition here removes a
    /// confident wrong answer.
    /// </remarks>
    private static readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase)
    {
        "alex", "alexis", "sam", "samantha", "sammy", "jordan", "casey", "jamie", "kim",
        "taylor", "morgan", "riley", "avery", "quinn", "rowan", "sage", "skyler", "bailey",
        "parker", "peyton", "reese", "river", "robin", "sasha", "shannon", "shawn", "stacy",
        "tracy", "val", "dana", "dale", "angel", "ariel", "ash", "ashley", "blake", "carey",
        "carol", "chris", "christian", "clare", "corey", "dakota", "devin", "dusty", "eden",
        "elliott", "emery", "finley", "frankie", "gabriel", "gray", "harley", "harper",
        "hayden", "indigo", "jesse", "jody", "kelly", "kendall", "lane", "leslie", "logan",
        "lynn", "marley", "michele", "mickey", "noel", "phoenix", "rachael", "randy",
        "remy", "rene", "sandy", "shiloh", "sydney", "terry", "toni", "tori", "trinity",
        "wren", "ari", "eli", "emerson", "harlow", "jules", "kris", "milan", "noor",
        "pat", "peri", "rae", "ronnie", "sky", "storm", "zion", "abed", "amal", "nour",
        "ayaan", "aayan", "eden", "jun", "yuki", "rai", "lin", "kim", "ali", "ahmed",
        "michel", "dominique", "claude", "andrea", "danielle", "nadia", "kamal", "sami",
    };

    /// <summary>Names that read as male in common English usage.</summary>
    private static readonly HashSet<string> Male = new(StringComparer.OrdinalIgnoreCase)
    {
        "aaron", "abdul", "abdullah", "adam", "adrian", "ahmad", "alan", "albert", "alberto",
        "alejandro", "alessandro", "alexander", "alfred", "alfonso", "ali", "allen", "alvin",
        "andre", "andreas", "andres", "andrew", "andy", "angelo", "anthony", "antonio",
        "archie", "armand", "arnold", "arthur", "asher", "austin", "barry", "ben", "benjamin",
        "bernard", "bill", "billy", "bobby", "brad", "bradley", "brandon", "brendan", "brent",
        "brett", "brian", "bruce", "bryan", "caleb", "calvin", "cameron", "carl", "carlo",
        "carlos", "carson", "cedric", "chad", "charles", "charlie", "chase", "chester",
        "christopher", "clark", "clayton", "clifford", "clint", "clyde", "cody", "colin",
        "connor", "craig", "curtis", "cyrus", "dale", "damian", "damon", "daniel", "danny",
        "darrell", "darren", "daryl", "dave", "david", "dean", "dennis", "derek", "derrick",
        "desmond", "derek", "diego", "dominic", "donald", "douglas", "drew", "duane",
        "duncan", "dustin", "dylan", "earl", "eddie", "edgar", "edmund", "eduardo", "edward",
        "edwin", "elias", "elijah", "elliot", "emil", "emilio", "emmanuel", "enrique", "eric",
        "erik", "ernest", "ethan", "eugene", "evan", "fabian", "felipe", "felix", "fernando",
        "floyd", "francis", "francisco", "frank", "fred", "frederick", "gabriel", "garrett",
        "gary", "gavin", "gene", "geoffrey", "george", "gerald", "gilbert", "glen", "glenn",
        "gordon", "grant", "gregory", "guy", "harold", "harrison", "harry", "harvey", "hector",
        "henry", "herbert", "howard", "hugo", "ian", "ignacio", "isaac", "isaiah", "ivan",
        "jack", "jackson", "jacob", "jake", "james", "jared", "jason", "javier", "jay",
        "jeffrey", "jeremy", "jerome", "jerry", "jesse", "jesus", "jim", "jimmy", "joel",
        "john", "johnny", "jon", "jonah", "jonathan", "jose", "joseph", "joshua", "juan",
        "julian", "julio", "justin", "karl", "keith", "kelvin", "ken", "kenneth", "kenny",
        "kevin", "kirk", "kurt", "kyle", "lance", "larry", "lawrence", "lee", "leo", "leon",
        "leonard", "leroy", "lewis", "liam", "lionel", "lloyd", "logan", "lonnie", "louis",
        "lucas", "luis", "luke", "manuel", "marc", "marcus", "mario", "mark", "marshall",
        "martin", "marvin", "mason", "mathew", "matt", "matthew", "maurice", "max", "maxwell",
        "melvin", "micah", "michael", "miguel", "mike", "miles", "milton", "mitch", "mitchell",
        "morris", "nate", "nathan", "nathaniel", "neil", "nelson", "nicholas", "nick", "nicolas",
        "nigel", "noah", "norman", "oliver", "omar", "orlando", "oscar", "otto", "owen",
        "pablo", "patrick", "paul", "pedro", "percy", "perry", "peter", "philip", "phillip",
        "pierre", "ralph", "ramon", "randall", "raphael", "raul", "ray", "raymond", "reginald",
        "ricardo", "richard", "rick", "ricky", "rob", "robert", "roberto", "rodney", "roger",
        "roland", "ronald", "ronnie", "rory", "ross", "roy", "russell", "ryan", "salvador",
        "samuel", "santiago", "scott", "sean", "sergio", "seth", "shane", "sidney", "simon",
        "spencer", "stanley", "stephen", "steve", "steven", "stuart", "ted", "terrence",
        "theodore", "thomas", "tim", "timothy", "todd", "tom", "tommy", "tony", "travis",
        "trevor", "tristan", "troy", "tyler", "tyrone", "ulysses", "victor", "vincent",
        "virgil", "wade", "wallace", "walter", "warren", "wayne", "wesley", "william",
        "willie", "wyatt", "xavier", "yusuf", "zachary", "zane",
    };

    /// <summary>Names that read as female in common English usage.</summary>
    private static readonly HashSet<string> Female = new(StringComparer.OrdinalIgnoreCase)
    {
        "abigail", "ada", "adele", "adriana", "agnes", "aisha", "alice", "alicia", "alina",
        "alisa", "alison", "allison", "alma", "alyssa", "amanda", "amber", "amelia", "amy",
        "ana", "anastasia", "andrea", "angela", "angelina", "anita", "ann", "anna", "anne",
        "annette", "annie", "antoinette", "april", "ariana", "arlette", "audrey", "aurora",
        "ava", "barbara", "beatrice", "becky", "belinda", "bella", "bernadette", "beth",
        "bethany", "betsy", "betty", "beverly", "bianca", "bonnie", "brenda", "brianna",
        "bridget", "brittany", "brooke", "camila", "candace", "carla", "carmen", "carole",
        "caroline", "carrie", "catherine", "cathy", "cecilia", "celeste", "charlotte",
        "chelsea", "cheryl", "chloe", "christina", "christine", "cindy", "claire", "clara",
        "claudia", "colleen", "connie", "constance", "crystal", "cynthia", "daisy", "daphne",
        "darlene", "dawn", "deanna", "debbie", "deborah", "debra", "delia", "denise", "diana",
        "diane", "dolores", "donna", "dora", "dorothy", "edith", "eileen", "elaine", "eleanor",
        "elena", "elisa", "elizabeth", "ella", "ellen", "elsa", "emily", "emma", "erica",
        "erin", "esther", "eva", "evelyn", "faith", "fatima", "fiona", "florence", "frances",
        "francesca", "gabriela", "gail", "gemma", "georgia", "gina", "gladys", "gloria",
        "grace", "greta", "gwen", "hailey", "hannah", "harriet", "heather", "heidi", "helena",
        "helen", "holly", "hope", "ida", "ines", "ingrid", "irene", "iris", "isabel",
        "isabella", "ivy", "jade", "jane", "janet", "janice", "janine", "jasmine", "jean",
        "jeanne", "jenna", "jennifer", "jenny", "jessica", "jill", "joan", "joanne", "josephine",
        "joyce", "judith", "judy", "julia", "julie", "june", "karen", "kate", "katherine",
        "kathleen", "kathryn", "kathy", "katie", "kayla", "keisha", "kendra", "kerry",
        "kristen", "kristin", "kristina", "laura", "lauren", "laurie", "leah", "lena",
        "leona", "lidia", "lila", "lilly", "lily", "linda", "lindsay", "lisa", "liz",
        "liza", "lois", "loretta", "lorraine", "louise", "lucy", "lucille", "luz",
        "lydia", "mabel", "madeline", "madison", "maggie", "malia", "margaret", "maria",
        "mariah", "marian", "marie", "marilyn", "marina", "marion", "marjorie", "martha",
        "mary", "maureen", "mavis", "maya", "megan", "melanie", "melissa", "melody", "meredith",
        "mia", "michelle", "mildred", "molly", "monica", "muriel", "myra", "nadia", "nancy",
        "naomi", "natalie", "natasha", "nora", "noreen", "olga", "olive", "olivia", "paige",
        "pamela", "patricia", "paula", "pauline", "pearl", "peggy", "penelope", "phoebe",
        "phyllis", "polly", "priscilla", "rachel", "rebecca", "regina", "rena", "rhonda",
        "rita", "roberta", "rosa", "rosalie", "rose", "rosemary", "roxanne", "ruby", "ruth",
        "sabrina", "sadie", "sally", "sandra", "sara", "sarah", "savannah", "scarlett",
        "selena", "shelly", "shirley", "sierra", "sofia", "sonia", "sophia", "sophie",
        "stella", "stephanie", "sue", "susan", "suzanne", "sylvia", "tabitha", "tamara",
        "tammy", "tanya", "tatiana", "teresa", "theresa", "tiffany", "tina", "tricia",
        "ursula", "valerie", "vanessa", "vera", "veronica", "victoria", "vicki", "vicky",
        "violet", "virginia", "vivian", "wendy", "whitney", "yasmine", "yvonne", "zara",
        "zoe",
    };

    /// <summary>
    /// Guesses a gender from a person's name.
    /// </summary>
    /// <param name="name">A display name or handle.</param>
    /// <returns>A guess, or null when the name is unknown, ambiguous, or not a name.</returns>
    /// <remarks>
    /// Only the first token is consulted. A handle like <c>abdullah_dabbagh_</c> yields its
    /// first token, which happens to work; a name like <c>_soup_can_2</c> yields nothing.
    /// </remarks>
    public static GenderPrediction? Guess(string? name)
    {
        var first = FirstToken(name);

        if (first is null)
        {
            return null;
        }

        var lowered = first.ToLowerInvariant();

        // Ambiguity is checked first, so a name in both lists can never produce a guess.
        if (Ambiguous.Contains(lowered))
        {
            return null;
        }

        if (Male.Contains(lowered))
        {
            return new GenderPrediction("M", first, 0.9);
        }

        if (Female.Contains(lowered))
        {
            return new GenderPrediction("F", first, 0.9);
        }

        return null;
    }

    /// <summary>
    /// The first plausible name token, or null.
    /// </summary>
    /// <remarks>
    /// Handles and display names arrive in many shapes. Tokens that are short, numeric, or
    /// clearly not letters are skipped rather than treated as names, since guessing from
    /// "x" or "2" would be noise.
    /// </remarks>
    private static string? FirstToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var token in name.Split([' ', '_', '-', '.', ',', '/', '@'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Letters only, at least two of them. A leading digit or symbol means it is part
            // of a handle rather than a name.
            if (token.Length < 2 || !token.All(char.IsLetter))
            {
                continue;
            }

            if (char.IsLower(token[0]) && token.Length > 20)
            {
                // A long lower-case run is almost certainly a concatenated handle rather than
                // a first name.
                continue;
            }

            return token;
        }

        return null;
    }

    /// <summary>How many names the table knows.</summary>
    public static int KnownNameCount => Male.Union(Female, StringComparer.OrdinalIgnoreCase).Count();
}
