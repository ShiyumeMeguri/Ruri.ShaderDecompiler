using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// Block-structured text writer for ShaderLab output.
///
/// ShaderLab is brace-nested and read by humans, so indentation is not cosmetic —
/// a mis-indented pass is unreadable at the sizes these files reach. Depth is
/// tracked here instead of threaded through every emit method.
/// </summary>
internal sealed class IndentedWriter
{
    private const int SpacesPerLevel = 4;

    private readonly StringBuilder _builder = new();
    private int _depth;

    public void Indent() => _depth++;

    public void Unindent()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }

    public void Line(string text)
    {
        if (text.Length == 0)
        {
            _builder.AppendLine();
            return;
        }

        _builder.Append(' ', _depth * SpacesPerLevel);
        _builder.AppendLine(text);
    }

    public void Blank() => _builder.AppendLine();

    /// <summary>Emit pre-formatted text verbatim, one line at a time, so it picks
    /// up no indentation of its own.</summary>
    public void Raw(string text)
    {
        foreach (string line in TextLines.Split(text))
        {
            Line(line);
        }
    }

    public override string ToString() => _builder.ToString();
}

/// <summary>Line-ending normalisation shared by the ShaderLab emitters.</summary>
internal static class TextLines
{
    /// <summary>Split on any line ending. Emitted source arrives from several
    /// backends with inconsistent endings; ShaderLab output should not.</summary>
    public static IEnumerable<string> Split(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    public static string TrimTrailingWhitespace(string text) => text.TrimEnd(' ', '\t', '\r', '\n');
}
