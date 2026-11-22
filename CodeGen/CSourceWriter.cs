using System.Globalization;
using System.Text;

namespace PspScript.CodeGen;

public sealed class CSourceWriter
{
    private readonly StringBuilder _sb = new(16 * 1024);
    private int _indentLevel;
    private const string IndentUnit = "    ";

    public void Indent() => _indentLevel++;

    public void Unindent()
    {
        if (_indentLevel > 0)
            _indentLevel--;
    }

    public void WriteLine()
    {
        _sb.AppendLine();
    }

    public void WriteLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            _sb.AppendLine();
            return;
        }

        WriteIndent();
        _sb.AppendLine(line);
    }

    public void WriteRaw(string text)
    {
        _sb.Append(text);
    }

    public void OpenBlock(string header = "")
    {
        if (!string.IsNullOrEmpty(header))
        {
            WriteIndent();
            _sb.Append(header);
            _sb.AppendLine(" {");
        }
        else
        {
            WriteLine("{");
        }
        Indent();
    }

    public void CloseBlock(string suffix = "")
    {
        Unindent();
        WriteIndent();
        _sb.Append('}');
        if (!string.IsNullOrEmpty(suffix))
        {
            _sb.Append(suffix);
        }
        _sb.AppendLine();
    }

    public void WriteSectionHeader(string title)
    {
        _sb.AppendLine();
        WriteLine("/* ========================================================================== */");
        WriteLine($"/* {title,-74} */");
        WriteLine("/* ========================================================================== */");
        _sb.AppendLine();
    }

    public static string FormatFloat(float value)
    {
        string formatted = value.ToString("0.0#####", CultureInfo.InvariantCulture);
        return $"{formatted}f";
    }

    public static string FormatColor(uint abgr)
    {
        return $"((psps_color_t)0x{abgr:X8}u)";
    }

    private void WriteIndent()
    {
        for (int i = 0; i < _indentLevel; i++)
        {
            _sb.Append(IndentUnit);
        }
    }

    public override string ToString() => _sb.ToString();
}
