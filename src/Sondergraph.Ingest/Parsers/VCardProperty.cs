namespace Sondergraph.Ingest.Parsers;

/// <summary>
/// One parsed vCard property line: <c>NAME;PARAM;PARAM:VALUE</c>.
/// </summary>
/// <remarks>
/// <para>
/// The value is split at the <em>first unescaped colon</em>, not the first colon. Base64
/// payloads and URLs contain colons, and a naive split at the first one truncates them.
/// </para>
/// <para>
/// Parameters differ by version and are normalised here:
/// vCard 2.1 writes <c>TEL;CELL</c> (bare values), 3.0 writes <c>TEL;TYPE=CELL</c>
/// (named). <see cref="ParametersFor"/> hides that difference from the parser, which is what
/// makes one parser able to handle both without branching everywhere.
/// </para>
/// </remarks>
internal sealed class VCardProperty
{
    private VCardProperty(string name, Dictionary<string, List<string>> parameters, string value)
    {
        Name = name;
        Parameters = parameters;
        Value = value;
    }

    /// <summary>Upper-cased property name, e.g. <c>TEL</c>.</summary>
    internal string Name { get; }

    /// <summary>Parameters, keyed case-insensitively.</summary>
    internal Dictionary<string, List<string>> Parameters { get; }

    /// <summary>The property value, after the first unescaped colon.</summary>
    internal string Value { get; }

    /// <summary>
    /// Gets parameter values, normalising the 2.1 versus 3.0 difference.
    /// </summary>
    /// <param name="name">Parameter name to look for, e.g. <c>TYPE</c>.</param>
    /// <param name="version">The card's declared vCard version.</param>
    /// <remarks>
    /// In 2.1, a bare parameter such as <c>CELL</c> is a type. In 3.0, it appears as
    /// <c>TYPE=CELL</c>. Callers ask for "TYPE" and get the effective types either way.
    /// </remarks>
    internal List<string> ParametersFor(string name, string version)
    {
        var results = new List<string>();

        if (Parameters.TryGetValue(name, out var explicitValues))
        {
            results.AddRange(explicitValues);
        }

        // 2.1 bare parameters: anything that is not a known structural parameter and has no
        // value of its own is a type.
        if (version.StartsWith('2'))
        {
            foreach (var (key, values) in Parameters)
            {
                if (IsStructural2xParameter(key))
                {
                    continue;
                }

                results.AddRange(values.Count == 0 ? [key] : values);
            }
        }

        return results;
    }

    /// <summary>
    /// Parameters in 2.1 that are not types: encoding, charset, language and value type.
    /// </summary>
    private static bool IsStructural2xParameter(string key) =>
        key.Equals("ENCODING", StringComparison.OrdinalIgnoreCase)
        || key.Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
        || key.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TYPE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a property line, or returns null when it is not a property.</summary>
    internal static VCardProperty? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // Find the first colon that is not part of a group prefix and not escaped.
        var colon = -1;
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == ':')
            {
                colon = i;
                break;
            }
        }

        if (colon < 0)
        {
            // A property with no value is legal; treat the whole line as the name.
            return new VCardProperty(line.Trim().ToUpperInvariant(), [], string.Empty);
        }

        var head = line[..colon];
        var value = line[(colon + 1)..];

        // A group prefix (GROUP.PROPERTY) is legal but unused here; strip it.
        var dot = head.IndexOf('.');
        if (dot >= 0)
        {
            head = head[(dot + 1)..];
        }

        var segments = SplitOnUnescaped(head, ';');
        var name = segments[0].Trim().ToUpperInvariant();
        var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i].Trim();

            if (segment.Length == 0)
            {
                continue;
            }

            var equals = segment.IndexOf('=');

            if (equals < 0)
            {
                // 2.1 bare parameter: the parameter IS the value.
                parameters[segment] = [];
                continue;
            }

            var key = segment[..equals].Trim();
            var parameterValue = segment[(equals + 1)..].Trim().Trim('"');

            if (!parameters.TryGetValue(key, out var list))
            {
                list = [];
                parameters[key] = list;
            }

            // 3.0 allows TYPE=a,b.
            foreach (var part in parameterValue.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                list.Add(part.Trim());
            }
        }

        return new VCardProperty(name, parameters, value);
    }

    /// <summary>Splits on a separator, respecting backslash escapes and quotes.</summary>
    private static List<string> SplitOnUnescaped(string value, char separator)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;
        var quoted = false;

        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            switch (character)
            {
                case '\\':
                    escaped = true;
                    break;

                case '"':
                    quoted = !quoted;
                    break;

                default:
                    if (character == separator && !quoted)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(character);
                    }

                    break;
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
