const backendUrl = 'https://o3fjwzazcg.execute-api.us-east-1.amazonaws.com/dev/v1/getCurrentWeather';

const rootElement = document.getElementById('inputString');
const tempElement = document.querySelector('.weather-main-temp');
const cloudElement = document.querySelector('.weather-main-cloud');
const windElements = document.querySelectorAll('.weather-wind-row');
const speedElements = windElements[0].querySelector('.weather-wind-row-value');
const directionElements = windElements[1].querySelector('.weather-wind-row-value');
const pressureElements = windElements[2].querySelector('.weather-wind-row-value');
const humidityElements = windElements[3].querySelector('.weather-wind-row-value');

rootElement.value = 'London';

async function getCurrentWeather() {
    const cityValue = rootElement.value;
    const queryParams = {
        city: cityValue
    };

    let res = await axios.get(backendUrl, { params: queryParams }).catch(err => 
    {
        tempElement.innerText = '';
        cloudElement.innerText = '';
    
        speedElements.innerText = '';
        directionElements.innerText = '';
        pressureElements.innerText = '';
        humidityElements.innerText = '';
    });
    
    console.log(res);
    let data = res.data;

    tempElement.innerText = `${data.city}, ${data.temperature}°F`
    cloudElement.innerText = data.weatherCondition.type;

    speedElements.innerText = `${data.wind.speed} m/s`;
    directionElements.innerText = data.wind.direction;
    pressureElements.innerText = `${data.weatherCondition.pressure} hPa`;
    humidityElements.innerText = `${data.weatherCondition.humidity} %`;
}