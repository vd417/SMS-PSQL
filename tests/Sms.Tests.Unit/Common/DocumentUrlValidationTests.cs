using FluentAssertions;
using Sms.Application.Common;
using Xunit;

namespace Sms.Tests.Unit.Common;

public class DocumentUrlValidationTests
{
    [Fact]
    public void Accepts_pdf_image_and_http_urls()
    {
        DocumentUrlValidation.Validate("data:application/pdf;base64,JVBERi0=").Should().BeNull();
        DocumentUrlValidation.Validate("data:image/png;base64,iVBOR").Should().BeNull();
        DocumentUrlValidation.Validate("data:image/jpeg;base64,/9j/4A").Should().BeNull();
        DocumentUrlValidation.Validate("https://example.com/id.pdf").Should().BeNull();
        DocumentUrlValidation.Validate(null).Should().BeNull();
        DocumentUrlValidation.Validate("").Should().BeNull();
    }

    [Fact]
    public void Rejects_oversize_document()
    {
        var big = "data:application/pdf;base64," + new string('A', 3_500_001);
        DocumentUrlValidation.Validate(big).Should().NotBeNull();
    }

    [Fact]
    public void Rejects_wrong_prefix()
    {
        DocumentUrlValidation.Validate("data:text/plain;base64,QQ==").Should().NotBeNull();
        DocumentUrlValidation.Validate("ftp://x/y.pdf").Should().NotBeNull();
    }

    [Fact]
    public void Normalize_trims_and_blanks_to_null()
    {
        DocumentUrlValidation.Normalize("   ").Should().BeNull();
        DocumentUrlValidation.Normalize(null).Should().BeNull();
        DocumentUrlValidation.Normalize(" data:image/png;base64,A ").Should().Be("data:image/png;base64,A");
    }
}
