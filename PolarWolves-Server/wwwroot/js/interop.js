// ReSharper disable Html.EventNotResolved
if (sortable == undefined) {
	window.notification.new({
		type: "error",
        title: "",
		message: "A critical component could not be loaded (sorter). Please restart the application!",
		renderTo: "toastarea",
		duration: 10000
	});
	sortable = null;
}

$(function () {
    /*
     * Prevents default browser navigation (Often causes breaks in code by somehow keeping state)
     * Can't seem to do this via the WebView2 component directly as key presses just don't reach the app...
     * So, instead the mouse back button is handled here.
     * Don't know where I could handle a keyboard back button... Because I can't find a JS key for it.
     *
     * tldr: pressing mouse back, or keyboard back often somewhat reliably causes the error in-app, and should be handled differently.
     */
    $(document).bind("mouseup", (e) => {
        if (e.which === 4 || e.which === 5) { // Backward & Forward mouse button
            e.preventDefault();
        }

        if (e.which === 4) { // Backward mouse button
            btnBack_Click();
            return;
        }
    });
});

function jQueryAppend(jQuerySelector, strToInsert) {
	$(jQuerySelector).append(strToInsert);
}

function jQueryProcessAccListSize() {
    let maxHeight = 0;
    $(".acc_list_item label").each((_, e) => { maxHeight = Math.max(maxHeight, e.offsetHeight); });
    if (document.getElementById("acc_list"))
	    document.getElementById("acc_list").setAttribute("style", `grid-template-rows: repeat(auto-fill, ${maxHeight}px)`);
}

let accListRectsBefore = {};
let accListMutationObserver = null;
const STEAM_CATALOG_STORAGE_KEY = "polarwolves_steam_catalog_state_v1";
let steamCatalogState = null;
let activeDraggedAccountId = "";

function isSteamCatalogPage() {
    return document.querySelector(".steam-rework") !== null && document.getElementById("acc_list") !== null;
}

function getSteamAccountIds() {
    return Array.from(document.querySelectorAll("#acc_list .acc_list_item input.acc"))
        .map((input) => input.id)
        .filter((id) => id !== "");
}

function getDefaultSteamCatalogState() {
    return {
        catalogs: [],
        accountMap: {},
        activeCatalogId: "__all"
    };
}

function createCatalogId() {
    return `cat_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function createFallbackCatalogName() {
    const existingNames = new Set((steamCatalogState?.catalogs ?? []).map((c) => (c.name ?? "").trim().toLowerCase()));
    let index = 1;
    let candidate = `Catalog ${index}`;
    while (existingNames.has(candidate.toLowerCase())) {
        index += 1;
        candidate = `Catalog ${index}`;
    }
    return candidate;
}

function loadSteamCatalogState() {
    if (steamCatalogState !== null) return;
    try {
        const raw = localStorage.getItem(STEAM_CATALOG_STORAGE_KEY);
        steamCatalogState = raw ? JSON.parse(raw) : getDefaultSteamCatalogState();
    } catch {
        steamCatalogState = getDefaultSteamCatalogState();
    }
}

function saveSteamCatalogState() {
    if (steamCatalogState === null) return;
    try {
        localStorage.setItem(STEAM_CATALOG_STORAGE_KEY, JSON.stringify(steamCatalogState));
    } catch {
        // ignore storage write failures
    }
}

function normalizeSteamCatalogState(accountIds = getSteamAccountIds()) {
    if (steamCatalogState === null || typeof steamCatalogState !== "object") {
        steamCatalogState = getDefaultSteamCatalogState();
    }

    const seenCatalogIds = new Set();
    const validCatalogs = [];
    (Array.isArray(steamCatalogState.catalogs) ? steamCatalogState.catalogs : []).forEach((catalog) => {
        if (!catalog || typeof catalog !== "object") return;
        const id = `${catalog.id ?? ""}`.trim();
        if (id === "" || seenCatalogIds.has(id)) return;
        const name = `${catalog.name ?? ""}`.trim();
        validCatalogs.push({
            id: id,
            name: name === "" ? createFallbackCatalogName() : name.slice(0, 40)
        });
        seenCatalogIds.add(id);
    });
    steamCatalogState.catalogs = validCatalogs;

    const validAccountIds = new Set(accountIds);
    const shouldPruneMissingAccounts = accountIds.length > 0;
    const validAccountMap = {};
    if (steamCatalogState.accountMap && typeof steamCatalogState.accountMap === "object") {
        Object.keys(steamCatalogState.accountMap).forEach((accId) => {
            const catalogId = `${steamCatalogState.accountMap[accId] ?? ""}`;
            if (shouldPruneMissingAccounts && !validAccountIds.has(accId)) return;
            if (!seenCatalogIds.has(catalogId)) return;
            validAccountMap[accId] = catalogId;
        });
    }
    steamCatalogState.accountMap = validAccountMap;

    const activeCatalogId = `${steamCatalogState.activeCatalogId ?? "__all"}`;
    steamCatalogState.activeCatalogId = activeCatalogId === "__all" || seenCatalogIds.has(activeCatalogId)
        ? activeCatalogId
        : "__all";
}

function findCatalogById(catalogId) {
    return (steamCatalogState?.catalogs ?? []).find((catalog) => catalog.id === catalogId) ?? null;
}

function getCatalogAssignment(accountId) {
    return `${steamCatalogState?.accountMap?.[accountId] ?? ""}`;
}

function countCatalogAccounts(catalogId) {
    let count = 0;
    Object.keys(steamCatalogState?.accountMap ?? {}).forEach((accountId) => {
        if (steamCatalogState.accountMap[accountId] === catalogId) count += 1;
    });
    return count;
}

function setCatalogStatus(message) {
    const statusInput = document.getElementById("CurrentStatus");
    if (statusInput instanceof HTMLInputElement) {
        statusInput.value = message;
    }
}

function applySteamCatalogFilter() {
    if (!isSteamCatalogPage()) return;
    const activeCatalogId = steamCatalogState?.activeCatalogId ?? "__all";
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item) => {
        if (!(item instanceof HTMLElement)) return;
        const accId = getAccountCardId(item);
        const inCatalog = getCatalogAssignment(accId);
        const isVisible = activeCatalogId === "__all" || (inCatalog !== "" && inCatalog === activeCatalogId);
        item.classList.toggle("is-catalog-hidden", !isVisible);
    });

    jQueryProcessAccListSize();
}

function updateAccountCatalogBadges() {
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item) => {
        if (!(item instanceof HTMLElement)) return;
        const accountId = getAccountCardId(item);
        const label = item.querySelector("label.acc");
        if (!(label instanceof HTMLElement)) return;

        const assignedCatalogId = getCatalogAssignment(accountId);
        const assignedCatalog = assignedCatalogId !== "" ? findCatalogById(assignedCatalogId) : null;
        let badge = label.querySelector(".acc_catalog_badge");
        if (!(badge instanceof HTMLElement)) {
            badge = document.createElement("span");
            badge.className = "acc_catalog_badge";
            label.appendChild(badge);
        }

        if (assignedCatalog === null) {
            badge.textContent = "";
            badge.classList.remove("is-visible");
            item.classList.remove("is-in-catalog");
            return;
        }

        badge.textContent = assignedCatalog.name;
        badge.classList.add("is-visible");
        item.classList.add("is-in-catalog");
    });
}

function setActiveCatalog(catalogId) {
    if (!isSteamCatalogPage()) return;
    const requested = `${catalogId ?? "__all"}`;
    steamCatalogState.activeCatalogId = requested === "__all" || findCatalogById(requested) !== null
        ? requested
        : "__all";
    saveSteamCatalogState();
    renderCatalogRail();
    applySteamCatalogFilter();
}

function removeCatalog(catalogId) {
    steamCatalogState.catalogs = steamCatalogState.catalogs.filter((catalog) => catalog.id !== catalogId);
    Object.keys(steamCatalogState.accountMap).forEach((accId) => {
        if (steamCatalogState.accountMap[accId] === catalogId) {
            delete steamCatalogState.accountMap[accId];
        }
    });
    if (steamCatalogState.activeCatalogId === catalogId) {
        steamCatalogState.activeCatalogId = "__all";
    }
    saveSteamCatalogState();
    renderCatalogRail();
    updateAccountCatalogBadges();
    applySteamCatalogFilter();
}

function renameCatalog(catalogId) {
    const catalog = findCatalogById(catalogId);
    if (catalog === null) return;
    const entered = prompt("Catalog name", catalog.name);
    if (entered === null) return;
    const nextName = entered.trim() === "" ? createFallbackCatalogName() : entered.trim().slice(0, 40);
    catalog.name = nextName;
    saveSteamCatalogState();
    renderCatalogRail();
    updateAccountCatalogBadges();
}

function assignAccountToCatalog(accountId, catalogId) {
    const id = `${accountId ?? ""}`.trim();
    if (id === "") return;

    if (catalogId === "__all" || catalogId === "") {
        delete steamCatalogState.accountMap[id];
        setCatalogStatus("Moved account back to All");
    } else {
        if (findCatalogById(catalogId) === null) return;
        steamCatalogState.accountMap[id] = catalogId;
        const catalog = findCatalogById(catalogId);
        setCatalogStatus(`Moved account to ${catalog?.name ?? "Catalog"}`);
    }

    saveSteamCatalogState();
    renderCatalogRail();
    updateAccountCatalogBadges();
    applySteamCatalogFilter();
}

function bindCatalogChipDropEvents(chipElement, catalogId) {
    if (!(chipElement instanceof HTMLElement)) return;
    chipElement.addEventListener("dragover", (event) => {
        if (activeDraggedAccountId === "") return;
        event.preventDefault();
        chipElement.classList.add("is-drop-target");
    });

    chipElement.addEventListener("dragleave", () => {
        chipElement.classList.remove("is-drop-target");
    });

    chipElement.addEventListener("drop", (event) => {
        event.preventDefault();
        chipElement.classList.remove("is-drop-target");

        const fromTransfer = event.dataTransfer?.getData("text/polarwolves-account-id") ?? "";
        const accountId = `${fromTransfer || activeDraggedAccountId}`.trim();
        if (accountId === "") return;
        assignAccountToCatalog(accountId, catalogId);
    });
}

function buildCatalogChip(catalogId, catalogName, count, isAll = false) {
    const chip = document.createElement("div");
    chip.className = `catalog_chip${isAll ? " is-static-all" : ""}`;
    chip.dataset.catalogId = catalogId;

    if ((steamCatalogState?.activeCatalogId ?? "__all") === catalogId) {
        chip.classList.add("is-active");
    }

    chip.innerHTML = `
        <span class="catalog_chip_icon"><i class="fas ${isAll ? "fa-layer-group" : "fa-folder"}"></i></span>
        <span class="catalog_chip_name">${catalogName}</span>
        <span class="catalog_chip_count">${count}</span>
        ${isAll ? "" : `<button type="button" class="catalog_chip_rename" title="Rename"><i class="fas fa-pen"></i></button>
        <button type="button" class="catalog_chip_delete" title="Delete"><i class="fas fa-times"></i></button>`}
    `;

    chip.addEventListener("click", (event) => {
        const target = event.target;
        if (target instanceof HTMLElement && target.closest(".catalog_chip_rename, .catalog_chip_delete")) return;
        setActiveCatalog(catalogId);
    });

    if (!isAll) {
        const renameButton = chip.querySelector(".catalog_chip_rename");
        const deleteButton = chip.querySelector(".catalog_chip_delete");
        if (renameButton instanceof HTMLElement) {
            renameButton.addEventListener("click", (event) => {
                event.preventDefault();
                event.stopPropagation();
                renameCatalog(catalogId);
            });
        }
        if (deleteButton instanceof HTMLElement) {
            deleteButton.addEventListener("click", (event) => {
                event.preventDefault();
                event.stopPropagation();
                if (!confirm("Delete this catalog? Accounts will go back to All.")) return;
                removeCatalog(catalogId);
            });
        }
    }

    bindCatalogChipDropEvents(chip, catalogId);
    return chip;
}

function initCatalogRailSortable() {
    const catalogRail = document.getElementById("catalog_rail");
    if (!(catalogRail instanceof HTMLElement) || catalogRail.dataset.sortableBound === "1") return;
    catalogRail.dataset.sortableBound = "1";

    sortable("#catalog_rail", {
        forcePlaceholderSize: true,
        placeholderClass: "placeHolderCatalog",
        hoverClass: "catalogHover",
        items: ".catalog_chip:not(.is-static-all)"
    });

    sortable("#catalog_rail")[0].addEventListener("sortupdate", () => {
        const catalogById = new Map((steamCatalogState?.catalogs ?? []).map((catalog) => [catalog.id, catalog]));
        const newOrder = [];
        document.querySelectorAll("#catalog_rail .catalog_chip:not(.is-static-all)").forEach((chip) => {
            if (!(chip instanceof HTMLElement)) return;
            const catalogId = `${chip.dataset.catalogId ?? ""}`;
            const catalog = catalogById.get(catalogId);
            if (catalog) newOrder.push(catalog);
        });

        steamCatalogState.catalogs = newOrder;
        saveSteamCatalogState();
        renderCatalogRail();
    });
}

function renderCatalogRail() {
    if (!isSteamCatalogPage()) return;
    const catalogRail = document.getElementById("catalog_rail");
    if (!(catalogRail instanceof HTMLElement)) return;

    catalogRail.innerHTML = "";
    const allCount = getSteamAccountIds().length;
    catalogRail.appendChild(buildCatalogChip("__all", "All Accounts", allCount, true));

    steamCatalogState.catalogs.forEach((catalog) => {
        catalogRail.appendChild(buildCatalogChip(catalog.id, catalog.name, countCatalogAccounts(catalog.id), false));
    });

    initCatalogRailSortable();
}

function initSteamAccountCatalogDrag() {
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item) => {
        if (!(item instanceof HTMLElement) || item.dataset.catalogDragBound === "1") return;
        item.dataset.catalogDragBound = "1";

        item.addEventListener("dragstart", (event) => {
            const accountId = getAccountCardId(item);
            if (accountId === "") return;

            activeDraggedAccountId = accountId;
            item.classList.add("is-catalog-dragging");
            try {
                event.dataTransfer?.setData("text/polarwolves-account-id", accountId);
                event.dataTransfer?.setData("text/plain", accountId);
            } catch {
                // ignore drag transfer errors
            }
        });

        item.addEventListener("dragend", () => {
            activeDraggedAccountId = "";
            item.classList.remove("is-catalog-dragging");
            document.querySelectorAll(".catalog_chip.is-drop-target").forEach((chip) => chip.classList.remove("is-drop-target"));
        });
    });
}

function addCatalogButtonClick() {
    if (!isSteamCatalogPage()) return;
    loadSteamCatalogState();
    normalizeSteamCatalogState();

    const entered = prompt("Catalog name", createFallbackCatalogName());
    if (entered === null) return;

    const name = entered.trim() === "" ? createFallbackCatalogName() : entered.trim().slice(0, 40);
    steamCatalogState.catalogs.push({
        id: createCatalogId(),
        name: name
    });

    saveSteamCatalogState();
    renderCatalogRail();
}

function initSteamCatalogs() {
    if (!isSteamCatalogPage()) return;

    loadSteamCatalogState();
    normalizeSteamCatalogState();
    renderCatalogRail();
    updateAccountCatalogBadges();
    applySteamCatalogFilter();
    initSteamAccountCatalogDrag();
}

function getAccountCardId(item) {
    const input = item.querySelector("input.acc");
    return input ? input.id : "";
}

function captureAccListRects() {
    accListRectsBefore = {};
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item) => {
        const accId = getAccountCardId(item);
        if (accId === "") return;
        accListRectsBefore[accId] = item.getBoundingClientRect();
    });
}

function animateAccListReorder() {
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item) => {
        const accId = getAccountCardId(item);
        if (accId === "" || !accListRectsBefore[accId]) return;

        const firstRect = accListRectsBefore[accId];
        const lastRect = item.getBoundingClientRect();
        const deltaX = firstRect.left - lastRect.left;
        const deltaY = firstRect.top - lastRect.top;
        if (Math.abs(deltaX) < 1 && Math.abs(deltaY) < 1) return;

        item.animate(
            [
                { transform: `translate(${deltaX}px, ${deltaY}px)` },
                { transform: "translate(0, 0)" }
            ],
            {
                duration: 280,
                easing: "cubic-bezier(0.22, 0.61, 0.36, 1)"
            }
        );
    });
}

function refreshAccountEntranceOrder() {
    document.querySelectorAll("#acc_list .acc_list_item").forEach((item, index) => {
        item.style.setProperty("--enter-order", `${Math.min(index, 14) * 28}ms`);
    });
}

function initAccListObserver() {
    const accList = document.getElementById("acc_list");
    if (!accList) return;

    if (accListMutationObserver !== null) {
        accListMutationObserver.disconnect();
    }

    accListMutationObserver = new MutationObserver((mutations) => {
        let hasNewCards = false;
        mutations.forEach((mutation) => {
            mutation.addedNodes.forEach((node) => {
                if (!(node instanceof HTMLElement) || !node.classList.contains("acc_list_item")) return;
                hasNewCards = true;
                node.classList.remove("is-entering");
                requestAnimationFrame(() => {
                    node.classList.add("is-entering");
                    setTimeout(() => node.classList.remove("is-entering"), 460);
                });
            });
        });

        if (hasNewCards) {
            refreshAccountEntranceOrder();
            initSteamAccountCardMotion();
            initSteamCatalogs();
        }
    });

    accListMutationObserver.observe(accList, { childList: true });
}

function resetPlatformCardMotion(card) {
    card.style.setProperty("--card-tilt-x", "0deg");
    card.style.setProperty("--card-tilt-y", "0deg");
    card.style.setProperty("--card-mx", "50%");
    card.style.setProperty("--card-my", "50%");
    card.style.setProperty("--card-glow", "0");
    card.style.setProperty("--card-sheen-shift", "-26%");
}

function getMotionProfile(profileName) {
    if (profileName === "steam") {
        return {
            tiltX: 18,
            tiltY: 20,
            glowBase: 0.2,
            glowEdge: 0.5,
            glowMax: 0.86,
            sheenRange: 46,
            sheenOffset: 0
        };
    }

    return {
        tiltX: 12,
        tiltY: 14,
        glowBase: 0.16,
        glowEdge: 0.45,
        glowMax: 0.74,
        sheenRange: 38,
        sheenOffset: -2
    };
}

function applyPlatformCardMotion(card, clientX, clientY, profileName = "default") {
    const rect = card.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;
    const profile = getMotionProfile(profileName);

    const px = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
    const py = Math.min(1, Math.max(0, (clientY - rect.top) / rect.height));
    const centerX = (px - 0.5) * 2;
    const centerY = (py - 0.5) * 2;
    const easedX = Math.sign(centerX) * Math.pow(Math.abs(centerX), 0.86);
    const easedY = Math.sign(centerY) * Math.pow(Math.abs(centerY), 0.86);

    const tiltX = (-easedY) * profile.tiltX;
    const tiltY = easedX * profile.tiltY;
    const glow = Math.min(profile.glowMax, profile.glowBase + Math.abs(centerX) * profile.glowEdge + Math.abs(centerY) * profile.glowEdge);
    const sheenShift = (centerX * (profile.sheenRange * 0.5)) + profile.sheenOffset;

    card.style.setProperty("--card-tilt-x", `${tiltX.toFixed(2)}deg`);
    card.style.setProperty("--card-tilt-y", `${tiltY.toFixed(2)}deg`);
    card.style.setProperty("--card-mx", `${(px * 100).toFixed(2)}%`);
    card.style.setProperty("--card-my", `${(py * 100).toFixed(2)}%`);
    card.style.setProperty("--card-glow", glow.toFixed(3));
    card.style.setProperty("--card-sheen-shift", `${sheenShift.toFixed(2)}%`);
}

function bindCardMotion(bindElement, motionElement = null, profileName = "default") {
    if (!(bindElement instanceof HTMLElement)) return;
    if (bindElement.dataset.motionBound === "1") return;

    const target = motionElement instanceof HTMLElement ? motionElement : bindElement;
    bindElement.dataset.motionBound = "1";
    resetPlatformCardMotion(target);

    let rafId = 0;
    let lastPointerMove = null;
    let willChangeReset = 0;

    const enableMotionLayer = () => {
        if (willChangeReset !== 0) {
            clearTimeout(willChangeReset);
            willChangeReset = 0;
        }
        target.style.willChange = "transform, box-shadow, filter, border-color";
    };

    const scheduleMotionLayerReset = () => {
        if (willChangeReset !== 0) clearTimeout(willChangeReset);
        willChangeReset = window.setTimeout(() => {
            target.style.removeProperty("will-change");
            willChangeReset = 0;
        }, 180);
    };

    const flushMove = () => {
        rafId = 0;
        if (lastPointerMove === null) return;
        applyPlatformCardMotion(target, lastPointerMove.clientX, lastPointerMove.clientY, profileName);
    };

    bindElement.addEventListener("pointerenter", (event) => {
        if (event.pointerType === "touch") return;
        enableMotionLayer();
        bindElement.classList.add("is-pointer-active");
        applyPlatformCardMotion(target, event.clientX, event.clientY, profileName);
    });

    bindElement.addEventListener("pointermove", (event) => {
        if (event.pointerType === "touch") return;
        enableMotionLayer();
        lastPointerMove = event;
        if (rafId !== 0) return;
        rafId = requestAnimationFrame(flushMove);
    });

    bindElement.addEventListener("pointerdown", (event) => {
        if (event.pointerType === "touch") return;
        enableMotionLayer();
        bindElement.classList.add("is-pressing");
    });

    const clearMotion = () => {
        bindElement.classList.remove("is-pointer-active");
        bindElement.classList.remove("is-pressing");
        resetPlatformCardMotion(target);
        scheduleMotionLayerReset();
    };

    bindElement.addEventListener("pointerleave", clearMotion);
    bindElement.addEventListener("pointerup", () => {
        bindElement.classList.remove("is-pressing");
        if (!bindElement.classList.contains("is-pointer-active")) scheduleMotionLayerReset();
    });
    bindElement.addEventListener("pointercancel", clearMotion);
}

function initPlatformCardMotion() {
    const cards = document.querySelectorAll(".main-platform-rework .platform_list_item");
    if (cards.length === 0) return;

    cards.forEach((card, index) => {
        if (!(card instanceof HTMLElement)) return;
        card.style.setProperty("--plat-order", `${Math.min(index, 18) * 62}ms`);
        bindCardMotion(card, card, "default");
    });
}

function initSteamAccountCardMotion() {
    const cards = document.querySelectorAll(".steam-rework #acc_list .acc_list_item");
    if (cards.length === 0) return;

    cards.forEach((card) => {
        if (!(card instanceof HTMLElement)) return;
        const motionTarget = card.querySelector("label.acc");
        if (!(motionTarget instanceof HTMLElement)) return;
        bindCardMotion(card, motionTarget, "steam");
    });
}

// Removes arguments like "?toast_type, &toast_title, &toast_message" from the URL.
function removeUrlArgs(argString) {
    const toRemove = argString.split(",");
	let url = window.location.href;
	if (url.indexOf("?") !== -1) {
		const parts = url.split("?");
		url = parts[0];
		const args = parts[1];
		let outArgs = "?";
		if (args.indexOf("&") !== -1) {
			args.split("&").forEach((i) => {
				if (i.indexOf("=") !== -1) {
					const key = i.split("=")[0];
					const val = i.split("=")[1];
					if (!toRemove.includes(key))
						outArgs += key + "=" + val + "&";
				} else {
					if (!toRemove.includes(i))
						outArgs += i + "&";
				}
			});
		}
		url += outArgs.slice(0, -1); // Remove last '&' or first '?'
	}
    history.pushState({}, null, url);
}

function updateStatus(status) {
    $("#CurrentStatus").val(status);
}

async function initPlatformListSortable() {
    // Create sortable list
    sortable(".platform_list", {
        forcePlaceholderSize: true,
        placeholderClass: "placeHolderPlat",
        hoverClass: "accountHover",
        items: ":not(toastarea)"
    });

    // On drag end, save list of items.
    sortable(".platform_list")[0].addEventListener("sortupdate", () => {
        var order = [];
        $(".platform_list > div").each((i, e) => { order.push(e.id); });

        DotNet.invokeMethodAsync("PolarWolves-Server", "GiSaveOrder", `Settings\\platformOrder.json`, JSON.stringify(order));
    });

    initPlatformCardMotion();
}

async function initAccListSortable() {
	if (document.getElementsByClassName("acc_list").length === 0) return;
    // Create sortable list
    sortable(".acc_list", {
        forcePlaceholderSize: true,
        placeholderClass: "placeHolderAcc",
        hoverClass: "accountHover",
        items: ":not(toastarea)"
    });
    // On drag start, un-select all items.
    sortable(".acc_list")[0].addEventListener("sortstart", () => {
        captureAccListRects();
        $("input:checked").each((_, e) => {
            $(e).prop("checked", false);
        });
    });

    var platformName = getCurrentPage();
    await DotNet.invokeMethodAsync("PolarWolves-Server", "GiCurrentBasicPlatform", platformName).then((r) => {
        platformName = r;
    });

    // On drag end, save list of items.
    sortable(".acc_list")[0].addEventListener("sortupdate", (e) => {
        const order = [];
        document.querySelectorAll(".acc_list .acc_list_item input.acc").forEach((input) => {
            if (!(input instanceof HTMLInputElement) || input.id === "") return;
            order.push(input.id);
        });

        DotNet.invokeMethodAsync("PolarWolves-Server", "GiSaveOrder", `LoginCache\\${platformName}\\order.json`, JSON.stringify(order));
        animateAccListReorder();
        refreshAccountEntranceOrder();
    });

    refreshAccountEntranceOrder();
    initAccListObserver();
    initSteamAccountCardMotion();
    initSteamCatalogs();
}

function steamAdvancedClearingAddLine(text) {
    queuedJQueryAppend("#lines", `<p>${text}</p>`);
}


function initEditor() {
    const editor = ace.edit("editor");
    editor.session.setMode("ace/mode/batchfile");
}

async function showUpdateBar() {
    $(".programMain").prepend(`<div class="updateBar"><span>${await GetLang("Update")}</span><i class="fas fa-times-circle" id="closeUpdateBar"></i></div>`);
    $(document).on("click", ".updateBar", function (event) {
        $(".updateBar").fadeOut();
        if (event.target.id !== "closeUpdateBar") {
            updateBarClick();
        }
    });
}
