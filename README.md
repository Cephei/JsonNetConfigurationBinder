# JsonNetConfigurationBinder
Add support for Json.net during Microsoft.Extensions.Configuration binding.

## USAGE: 
_I want to support Jace.net math expressions in my configuration._

#### Program.cs
```csharp
var config = new Configuration();
ctx.Configuration.BindJson("configuration", config);
```
#### appsettings.json
```csharp
{
  "configuration": {
    "backgroundJobWorkers": "processorCount",
    "crawlers": {
        "hyperlink": {
            "allowAutoRedirect": false,
            "maxConnectionsPerServer": 1000,
            "maxDegreeOfParallelism": "processorCount * 2",
            "maxMessagesPerTask": 10
        }
    }
  }
}
```

```csharp
public class Configuration
{
    [JsonConverter(typeof(JaceConverter))]
    public int BackgroundJobWorkers { get; set; }

    [JsonConverter(typeof(CrawlerSettingsConverter))]
    public Dictionary<string, object> Crawlers { get; set; }

    public TCrawlerSettings GetSettings<TCrawlerSettings>(string crawler)
        where TCrawlerSettings : class 
        => Crawlers?.GetValueOrDefault(crawler) as TCrawlerSettings;
}
```

```csharp
public class HyperlinkCrawlerSettings
{
    public bool AllowAutoRedirect { get; set; }

    public int MaxConnectionsPerServer { get; set; }

    [JsonConverter(typeof(JaceConverter))]
    public int MaxDegreeOfParallelism { get; set; }

    [JsonConverter(typeof(JaceConverter))]
    public int MaxMessagesPerTask { get; set; }
}
```
```csharp
public class CrawlerSettingsConverter : JsonConverter
{
    public override void WriteJson(
        JsonWriter writer, 
        object value, 
        JsonSerializer serializer)
    {
        throw new NotSupportedException();
    }

    public override object ReadJson(
        JsonReader reader, 
        Type objectType, 
        object existingValue,
        JsonSerializer serializer)
    {
        var dict = new Dictionary<string, object>();
        var jObject = JObject.Load(reader);
        foreach (var obj in jObject.Properties())
        {
            object settings = null;
            switch (obj.Name)
            {
                case "hyperlink":
                    settings =
                        JsonConvert.DeserializeObject<HyperlinkCrawlerSettings>(
                            obj.Value.ToString());
                    break;
                default:
                    throw new Exception("invalid crawler type");
            }

            dict.Add(obj.Name, settings);
        }
        return dict;
    }

    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(object);
    }

    public override bool CanWrite { get; } = false;
}
```
```csharp
public class JaceConverter : JsonConverter
{
    private static readonly Dictionary<string, double> _tokens = 
        new Dictionary<string, double>
        {
            { "processorCount", Environment.ProcessorCount }
        };

    private static readonly CalculationEngine _engine = new CalculationEngine();

    public override void WriteJson(
        JsonWriter writer, 
        object value, 
        JsonSerializer serializer)
    {
        throw new NotSupportedException();
    }

    public override object ReadJson(
        JsonReader reader, 
        Type objectType, 
        object existingValue,
        JsonSerializer serializer)
    {
        var expression = reader.Value.ToString();
        return Convert.ToInt32(_engine.Calculate(expression, _tokens));
    }

    public override bool CanWrite { get; } = false;

    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(int);
    }
}
```
