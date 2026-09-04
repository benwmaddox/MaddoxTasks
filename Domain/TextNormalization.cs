namespace MaddoxTasks.Domain;

internal static class TextNormalization
{
    /// <summary>
    /// Normalizes all line break variants (\n, \r, \r\n) to \r\n.
    /// </summary>
    internal static string NormalizeLineBreaks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // First collapse \r\n to \n, then convert lone \r to \n, then expand \n to \r\n.
        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "\r\n");
    }
}
