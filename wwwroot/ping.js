
window.ping = (dotNetHelper, n, timestamp)  => {
    // console.log('JS Ping!');
    dotNetHelper.invokeMethodAsync('Pong', n, timestamp);
}