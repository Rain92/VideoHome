
export function ping(dotNetHelper, n, timestamp) {
    dotNetHelper.invokeMethodAsync('Pong', n, timestamp);
}