public class OpenWeatherData
{
    public string Name { get; set; }
    public OpenWeatherBlock[] Weather { get; set; }
    public OpenMainBlock Main { get; set; }
    public OpenWindBlock Wind { get; set; }
}

public class OpenMainBlock
{
    public double Temp { get; set; }
    public double Pressure { get; set; }
    public double Humidity { get; set; }
}

public class OpenWeatherBlock
{
    public int Id { get; set; }
    public string Main { get; set; }
    public string Description { get; set; }
}

public class OpenWindBlock
{
    public double Speed { get; set; }
    public double Deg { get; set; }
}
