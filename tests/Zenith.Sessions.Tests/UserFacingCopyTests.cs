using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class UserFacingCopyTests
{
    // A lower-case word fused to a number ("up to120", "Queue format4") is a lost space,
    // not house style: product names such as "WebView2" start with a capital letter.
    private static readonly Regex FusedNumber=new(@"\b[a-z]{2,}\d",RegexOptions.CultureInvariant);
    // A sentence end fused to the next sentence ("goes to everyone.Capacity advice") is a lost space
    // where two pieces of copy were joined; the copy names no dotted identifiers, so none is exempt.
    private static readonly Regex FusedSentence=new(@"[a-z0-9)][.?!][A-Z]",RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CopyAttributes=new(StringComparer.Ordinal)
    {
        "Text","ToolTip","Content","Header","Title","AutomationProperties.Name","AutomationProperties.HelpText"
    };

    [Fact]
    public void ProductionWindowCopyKeepsSpacesBeforeNumbers()
    {
        var fused=Matches(FusedNumber);
        Assert.True(fused.Length==0,"User-facing copy is missing a space before a number: "+string.Join("; ",fused));
    }

    [Fact]
    public void ProductionWindowCopyKeepsSpacesBetweenSentences()
    {
        var fused=Matches(FusedSentence);
        Assert.True(fused.Length==0,"User-facing copy is missing a space after a sentence: "+string.Join("; ",fused));
    }

    private static string[] Matches(Regex pattern)
    {
        using var input=typeof(UserFacingCopyTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(input,LoadOptions.SetLineInfo);
        var attributeCopy=document.Descendants().SelectMany(element=>element.Attributes())
            .Where(attribute=>CopyAttributes.Contains(attribute.Name.LocalName) && !attribute.Value.StartsWith('{'))
            .Select(attribute=>(Line:((System.Xml.IXmlLineInfo)attribute).LineNumber,attribute.Value));
        var elementCopy=document.DescendantNodes().OfType<XText>()
            .Select(text=>(Line:((System.Xml.IXmlLineInfo)text).LineNumber,text.Value));
        return attributeCopy.Concat(elementCopy)
            .SelectMany(copy=>pattern.Matches(copy.Value).Select(match=>$"line {copy.Line}: '{Excerpt(copy.Value,match.Index)}'"))
            .ToArray();
    }

    private static string Excerpt(string value,int index)
    {
        int start=Math.Max(0,index-24);
        int end=Math.Min(value.Length,index+24);
        return value[start..end];
    }
}
