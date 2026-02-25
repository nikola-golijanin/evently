using Evently.Simulator;
using Evently.Simulator.Auth;
using Evently.Simulator.Bootstrap;
using Evently.Simulator.Clients;
using Evently.Simulator.State;
using Evently.Simulator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration));

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));

builder.Services.AddHttpClient();

builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddSingleton<SimulatorStateStore>();

builder.Services.AddSingleton<TokenService>();

builder.Services.AddSingleton<EventsClient>();
builder.Services.AddSingleton<UsersClient>();
builder.Services.AddSingleton<TicketingClient>();
builder.Services.AddSingleton<AttendanceClient>();

builder.Services.AddSingleton<SimulatorBootstrapper>();

builder.Services.AddHostedService<AdminWorker>();
builder.Services.AddHostedService<ShopperWorker>();
builder.Services.AddHostedService<AttendeeWorker>();

IHost host = builder.Build();

await host.Services.GetRequiredService<SimulatorBootstrapper>().RunAsync();

await host.RunAsync();
