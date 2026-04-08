if (chrome === undefined) {
	window.notification.new({
		type: "error",
        title: "",
		message: "A critical component could not be loaded (chrome). Please restart the application!",
		renderTo: "toastarea",
		duration: 10000
	});
	chrome = null;
}

const d = new Date();
var monthDay = "";

function getDate() {
    if (monthDay === "") monthDay = d.getMonth().toString() + d.getDate().toString();
    return monthDay;
}

const SysCommandSize = { // Reverses for april fools
    ScSizeHtLeft: (getDate() !== "31" ? 0xA : 0xB), // 1 + 9
    ScSizeHtRight: (getDate() !== "31" ? 0xB : 0xA),
    ScSizeHtTop: (getDate() !== "31" ? 0xC : 0xF),
    ScSizeHtTopLeft: (getDate() !== "31" ? 0xD : 0x11),
    ScSizeHtTopRight: (getDate() !== "31" ? 0xE : 0x10),
    ScSizeHtBottom: (getDate() !== "31" ? 0xF : 0xC),
    ScSizeHtBottomLeft: (getDate() !== "31" ? 0x10 : 0xE),
    ScSizeHtBottomRight: (getDate() !== "31" ? 0x11 : 0xD),

    ScMinimise: 0xF020,
    ScMaximise: 0xF030,
    ScRestore: 0xF120
};
const WindowNotifications = {
    WmClose: 0x0010
};

var possibleAnimations = [
    "Y",
    "X",
    "Z"
];

function btnBack_Click() {
    if (window.location.pathname === "/") {
        $("#btnBack i").css({ "transform": "rotate" + possibleAnimations[Math.floor(Math.random() * possibleAnimations.length)] + "(360deg)", "transition": "transform 500ms ease-in-out" });
        setTimeout(() => $("#btnBack i").css({ "transform": "", "transition": "transform 0 ease-in-out" }), 500);
    }
    else {
	    const tempUri = document.location.href.split("?")[0];
	    document.location.href = tempUri + (tempUri.endsWith("/") ? "../" : "/../");
    }
}

function hideWindow() {
    // Used in hide window on account switch
    if (navigator.appVersion.indexOf("PolarWolves") === -1) return;
    if (navigator.appVersion.indexOf("PolarWolves-CEF") !== -1) CefSharp.PostMessage({ "action": "HideWindow" });
    else chrome.webview.hostObjects.sync.eventForwarder.HideWindow();
}

function findDragRegionTarget(target) {
    let current = target;
    while (current && current !== document.body) {
        const appRegion = getComputedStyle(current)["-webkit-app-region"];
        if (appRegion === "no-drag") return null;
        if (appRegion === "drag") return current;
        current = current.parentElement;
    }

    const bodyRegion = getComputedStyle(document.body)["-webkit-app-region"];
    return bodyRegion === "drag" ? document.body : null;
}

function getDragRegionClass(target) {
    let current = target;
    while (current && current !== document.body) {
        if (current.classList && current.classList.contains("headerbar")) return "headerbar";

        if (current.classList) {
            if (current.classList.contains("resizeTopLeft")) return "resizeTopLeft";
            if (current.classList.contains("resizeTop")) return "resizeTop";
            if (current.classList.contains("resizeTopRight")) return "resizeTopRight";
            if (current.classList.contains("resizeRight")) return "resizeRight";
            if (current.classList.contains("resizeBottomRight")) return "resizeBottomRight";
            if (current.classList.contains("resizeBottom")) return "resizeBottom";
            if (current.classList.contains("resizeBottomLeft")) return "resizeBottomLeft";
            if (current.classList.contains("resizeLeft")) return "resizeLeft";
        }

        current = current.parentElement;
    }

    return "";
}

function handleWindowControls() {
    document.getElementById("btnBack").addEventListener("click", () => {
        btnBack_Click();
    });

    if (navigator.appVersion.indexOf("PolarWolves") === -1) return;

    if (navigator.appVersion.indexOf("PolarWolves-CEF") !== -1) {
        if (CefSharp === undefined) {
            window.notification.new({
                type: "error",
                title: "",
                message: "A critical component could not be loaded (CefSharp). Please restart the application!",
                renderTo: "toastarea",
                duration: 10000
            });
            CefSharp = null;
        }
        document.getElementById("btnMin").addEventListener("click", () => {
            CefSharp.PostMessage({ "action": "WindowAction", "value": SysCommandSize.ScMinimise });
        });

        document.getElementById("btnMax").addEventListener("click", () => {
            CefSharp.PostMessage({ "action": "WindowAction", "value": SysCommandSize.ScMaximise });
        });

        document.getElementById("btnRestore").addEventListener("click", () => {
            CefSharp.PostMessage({ "action": "WindowAction", "value": SysCommandSize.ScRestore });
        });

        document.getElementById("btnClose").addEventListener("click", () => {
            DotNet.invokeMethodAsync("PolarWolves-Server", "GetTrayMinimizeNotExit").then((r) => {
                if (r && !event.ctrlKey) { // If enabled, and NOT control held
                    CefSharp.PostMessage({ "action": "HideWindow" });
                } else {
                    CefSharp.PostMessage({ "action": "WindowAction", "value": WindowNotifications.WmClose });
                }
            });
        });

    }
    else // The normal WebView browser
    {
        document.getElementById("btnMin").addEventListener("click", () => {
            chrome.webview.hostObjects.sync.eventForwarder.WindowAction(SysCommandSize.ScMinimise);
        });

        document.getElementById("btnMax").addEventListener("click", () => {
            chrome.webview.hostObjects.sync.eventForwarder.WindowAction(SysCommandSize.ScMaximise);
        });

        document.getElementById("btnRestore").addEventListener("click", () => {
            chrome.webview.hostObjects.sync.eventForwarder.WindowAction(SysCommandSize.ScRestore);
        });

        document.getElementById("btnClose").addEventListener("click", () => {
            DotNet.invokeMethodAsync("PolarWolves-Server", "GetTrayMinimizeNotExit").then((r) => {
                if (r && !event.ctrlKey) { // If enabled, and NOT control held
                    chrome.webview.hostObjects.sync.eventForwarder.HideWindow();
                } else {
                    chrome.webview.hostObjects.sync.eventForwarder.WindowAction(WindowNotifications.WmClose);
                }
            });
        });
    }

    // For draggable regions:
    // https://github.com/MicrosoftEdge/WebView2Feedback/issues/200
    document.body.addEventListener("mousedown", (evt) => {
        // ES is actually 11, set in project file. This error can be ignored (if you see one about ES5)
        const dragTarget = findDragRegionTarget(evt.target);
        if (evt.button === 0 && dragTarget) {
            const c = getDragRegionClass(dragTarget);
            const value = (c === "resizeTopLeft" ? SysCommandSize.ScSizeHtTopLeft : (
                c === "resizeTop" ? SysCommandSize.ScSizeHtTop : (
                    c === "resizeTopRight" ? SysCommandSize.ScSizeHtTopRight : (
                        c === "resizeRight" ? SysCommandSize.ScSizeHtRight : (
                            c === "resizeBottomRight" ? SysCommandSize.ScSizeHtBottomRight : (
                                c === "resizeBottom" ? SysCommandSize.ScSizeHtBottom : (
                                    c === "resizeBottomLeft" ? SysCommandSize.ScSizeHtBottomLeft : (
                                        c === "resizeLeft" ? SysCommandSize.ScSizeHtLeft : 0))))))));

            if (navigator.appVersion.indexOf("PolarWolves-CEF") !== -1) {
                if (c === "headerbar" || value === 0) CefSharp.PostMessage({ "action": "MouseDownDrag" });
                else CefSharp.PostMessage({ "action": "MouseResizeDrag", "value": value });
            }
            else {
                if (value !== 0) chrome.webview.hostObjects.sync.eventForwarder.MouseResizeDrag(value);
                else chrome.webview.hostObjects.sync.eventForwarder.MouseDownDrag();
            }

            evt.preventDefault();
            evt.stopPropagation();
        }
    });
}
