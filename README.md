# Vintage RCON

Vintage RCON is a mod providing a Source RCON protocol implementation compliant with [Valve specification](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol).

## Config File

The config file is rather small and simple. The mod will generate it for you if it does not already exist.

```json
{
    "Port": 42425,
    "IP": null,
    "Password": "",
    "Timeout": 15
}
```

**Port** defines the port the server will listen on.

**IP** defines the IP address the server will listen on. If left to null, all IP addresses the server can interface with will be used.

**Password** is the RCON password. To comply with specification, the RCON server will not start if this is left to default empty value.

**Timeout** defines the RCON connexion timeout in minutes. This is an integer value. Minimum is 1.

## Build

Define you `VINTAGE_STORY` environment variable first.

Execute this command in the root of the projet directory:
```
dotnet build .
```
In `<project root>/bin/mods` a Zip archive will be produced with the name of the project with the configuration name appended. By default, this should be like "VintageRCON-Debug.zip".

## Thanks

Thanks to Th3Dilli for providing help injecting commands.

Thanks to SeveredSkullz for helping me sort out issues with the config file. You are a great rubber ducky!

