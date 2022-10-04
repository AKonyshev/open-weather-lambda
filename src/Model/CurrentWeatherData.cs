public class CurrentWeatherData
{
    public string City { get; set; }
    public double Temperature { get; set; }
    public WeatherConditionBlock WeatherCondition { get; set; }
    public WindBlock Wind { get; set; }
}

public class WeatherConditionBlock
{
    public string Type { get; set; }
    public double Pressure { get; set; }
    public double Humidity { get; set; }
}

public class WindBlock
{
    public double Speed { get; set; }
    public string Direction { get; set; }
}