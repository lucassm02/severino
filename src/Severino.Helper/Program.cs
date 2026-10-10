using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Severino.Contracts;
using Severino.Helper;

// Used by uninstall (elevated): drop both blocks and the approvals without going through the pipe.
// Lines outside Severino's blocks, edited or not, stay as they are.
if (args.Contains("--clear-hosts"))
{
    var hosts = new HostsFile(HostsFile.SystemPath);
    hosts.Write(Array.Empty<HostEntry>());
    hosts.WriteDns(Array.Empty<HostEntry>());
    var approvals = new DnsApprovals();
    // Before the approvals go: the NRPT rules and the saved wildcards.
    new Wildcards(new WildcardTable(), new PowerShellNrptRules(NullLogger<PowerShellNrptRules>.Instance), approvals, NullLogger<Wildcards>.Instance).Clear();
    approvals.Clear();
    return 0;
}

// Run elevated by the app, behind Windows' UAC prompt: approves public addresses for DNS entries.
if (args is ["--approve-dns", var encoded])
    return ApproveDns.Run(encoded);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Severino.Helper");

// Next to the binary: under Program Files only admins can write there, so a regular user cannot
// plant links for the SYSTEM process to write through.
builder.Services.AddSerilog(log => log
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "helper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7));

builder.Services.AddSingleton<IHostsWriter>(new HostsFile(HostsFile.SystemPath));
builder.Services.AddSingleton<IDnsApprovals>(new DnsApprovals());
builder.Services.AddSingleton<WildcardTable>();
builder.Services.AddSingleton<INrptRules, PowerShellNrptRules>();
builder.Services.AddSingleton(sp => new Wildcards(
    sp.GetRequiredService<WildcardTable>(), sp.GetRequiredService<INrptRules>(), sp.GetRequiredService<IDnsApprovals>(), sp.GetRequiredService<ILogger<Wildcards>>()));
builder.Services.AddSingleton(sp => new HelperRequestHandler(
    sp.GetRequiredService<IHostsWriter>(), sp.GetRequiredService<IDnsApprovals>(), sp.GetRequiredService<ILogger<HelperRequestHandler>>(),
    wildcards: sp.GetRequiredService<Wildcards>()));
builder.Services.AddHostedService(sp => new WildcardDnsServer(sp.GetRequiredService<WildcardTable>(), sp.GetRequiredService<ILogger<WildcardDnsServer>>()));
builder.Services.AddHostedService<PipeServer>();

var app = builder.Build();
// The DNS-entry wildcards of the last run answer again before the app comes back.
app.Services.GetRequiredService<Wildcards>().Load();
app.Run();
return 0;
