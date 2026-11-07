using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class AccountGroupCatalogContractTests
{
    [Theory]
    [InlineData(null,null)]
    [InlineData("#40b8a0","#40B8A0")]
    [InlineData("  #Aa00fF  ","#AA00FF")]
    public void ColorIsAnOptionalCanonicalRgbValue(string? input,string? expected)
        =>Assert.Equal(expected,AccountGroupDefinition.ValidateColor(input));

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("40B8A0")][InlineData("#abc")]
    [InlineData("#12345678")][InlineData("#GG0000")][InlineData("#１２３４５６")]
    [InlineData("red")][InlineData("#40B8A0\0")]
    public void InvalidColorsCannotBecomeStoredPresentationValues(string input)
        =>Assert.Throws<ArgumentException>(()=>AccountGroupDefinition.ValidateColor(input));

    [Fact]
    public void DefinitionRetainsStableIdentityAndOnlyCanonicalizesPresentation()
    {
        var row=new AccountGroupDefinition(Guid.NewGuid(),"  Raid  ","#40b8a0",42,3,DateTimeOffset.UtcNow);
        Assert.Equal(row with{Name="Raid",ColorHex="#40B8A0"},row.Validated());
        Assert.Throws<ArgumentException>(()=>(row with{Id=Guid.Empty}).Validated());
        Assert.Throws<ArgumentException>(()=>(row with{SortOrder=-1}).Validated());
        Assert.Throws<ArgumentException>(()=>(row with{Revision=0}).Validated());
        Assert.Throws<ArgumentException>(()=>(row with{UpdatedUtc=default}).Validated());
    }

    [Fact]
    public void CatalogQueriesAreBoundedWithoutCappingStoredDefinitions()
    {
        Assert.Equal(new AccountGroupCatalogQuery("Raid",10000000,1000),new AccountGroupCatalogQuery("  Raid  ",10000000,1000).Validated());
        Assert.Equal("",new AccountGroupCatalogQuery("  ").Validated().Search);
        foreach(var query in new[]{new AccountGroupCatalogQuery(null!),new("line\nbreak"),new(new string('x',61)),
            new(Page:-1),new(Page:10000001),new(PageSize:0),new(PageSize:1001)})
            Assert.Throws<ArgumentException>(()=>query.Validated());
    }
}
