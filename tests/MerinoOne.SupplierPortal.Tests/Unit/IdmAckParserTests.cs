using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R8 — TSD R8 §5.5 / D-R8-21/22/23. Pure tests for the IDM XML ack parser: pid/pid2/id/version extraction on
/// success, 4xx → Validation (surface detail), 5xx → Transient, malformed/no-pid 2xx → Validation (never a silent
/// success). Namespaced (http://infor.com/daf) and bare element names both resolve.
/// </summary>
public class IdmAckParserTests
{
    private readonly IdmAckParser _parser = new();
    private const string Ns = "http://infor.com/daf";

    [Fact]
    public void Success_item_extracts_pid_pid2_id_version()
    {
        var xml = $"<item xmlns=\"{Ns}\"><pid>MDS-abc123-LATEST</pid><pid2>1111-2222</pid2><id>MDS-abc123</id><version>3</version></item>";
        var ack = _parser.Parse(200, xml);

        ack.Failure.Should().Be(IdmFailureClass.None);
        ack.Pid.Should().Be("MDS-abc123-LATEST");
        ack.Pid2.Should().Be("1111-2222");
        ack.Id.Should().Be("MDS-abc123");
        ack.Version.Should().Be("3");
    }

    [Fact]
    public void Bare_namespaceless_item_still_parses()
    {
        var ack = _parser.Parse(200, "<item><pid>MDS-x-LATEST</pid></item>");
        ack.Failure.Should().Be(IdmFailureClass.None);
        ack.Pid.Should().Be("MDS-x-LATEST");
    }

    [Fact]
    public void Validation_4xx_error_is_terminal_with_detail()
    {
        var xml = $"<error xmlns=\"{Ns}\"><detail>File name \"null\"</detail></error>";
        var ack = _parser.Parse(400, xml);

        ack.Failure.Should().Be(IdmFailureClass.Validation);
        ack.Pid.Should().BeNull();
        ack.Detail.Should().Contain("File name");
    }

    [Fact]
    public void Server_5xx_is_transient()
    {
        var ack = _parser.Parse(503, "<error><detail>upstream down</detail></error>");
        ack.Failure.Should().Be(IdmFailureClass.Transient);
    }

    [Fact]
    public void Malformed_2xx_without_pid_is_validation_not_silent_success()
    {
        _parser.Parse(200, $"<item xmlns=\"{Ns}\"></item>").Failure.Should().Be(IdmFailureClass.Validation);
        _parser.Parse(200, "not xml at all").Failure.Should().Be(IdmFailureClass.Validation);
    }

    [Fact]
    public void Error_body_with_2xx_envelope_is_still_validation()
    {
        var ack = _parser.Parse(200, $"<error xmlns=\"{Ns}\"><detail>Entity does not exist</detail></error>");
        ack.Failure.Should().Be(IdmFailureClass.Validation);
        ack.Detail.Should().Contain("does not exist");
    }
}
