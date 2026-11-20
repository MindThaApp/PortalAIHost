using System.Net; using System.Net.Sockets;
var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
var port = ((IPEndPoint)l.LocalEndpoint).Port;
Console.WriteLine($"PORT={port} DLL={D.Hello()} EXE={Environment.ProcessPath}");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
