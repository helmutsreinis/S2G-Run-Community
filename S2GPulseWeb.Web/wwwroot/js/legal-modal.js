// Legal modal scroll detection
window.legalModalScroll = {
    checkScrolledToBottom: function (elementId, dotNetHelper) {
        const element = document.getElementById(elementId);
        if (element) {
            const isAtBottom = element.scrollHeight - element.scrollTop <= element.clientHeight + 50;
            if (isAtBottom) {
                dotNetHelper.invokeMethodAsync('OnScrolledToBottom');
            }
        }
    },
    
    attachScrollListener: function (elementId, dotNetHelper) {
        const element = document.getElementById(elementId);
        if (element) {
            element.addEventListener('scroll', function () {
                const isAtBottom = element.scrollHeight - element.scrollTop <= element.clientHeight + 50;
                if (isAtBottom) {
                    dotNetHelper.invokeMethodAsync('OnScrolledToBottom');
                }
            });
        }
    }
};
