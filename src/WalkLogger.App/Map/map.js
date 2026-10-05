/* WalkLogger: observed paths are separate from missing intervals and comparison paths. */
"use strict";
const map = L.map("map", { zoomControl: true, attributionControl: true }).setView([35.6812, 139.7671], 13);
const routes = L.featureGroup().addTo(map);
const previous = L.featureGroup().addTo(map);
const markers = L.featureGroup().addTo(map);
let tiles;
let tileErrors = 0;

function enableTiles() {
  if (tiles) return;
  tiles = L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap contributors</a>',
    updateWhenIdle: true
  }).addTo(map);
  tiles.on("tileerror", () => {
    tileErrors++;
    const notice = document.getElementById("notice");
    notice.hidden = false;
    notice.textContent = "背景地図を取得できません。ネット接続を確認してください。GPSルートは表示できます。";
  });
  tiles.on("loading", () => { tileErrors = 0; });
  tiles.on("load", () => { if (!tileErrors) document.getElementById("notice").hidden = true; });
}

function render(payload) {
  routes.clearLayers();
  previous.clearLayers();
  markers.clearLayers();
  if (!payload.segments?.length) return;
  enableTiles();
  for (const segment of payload.segments) {
    if (segment.length > 1) L.polyline(segment, { color: "#147d78", weight: 5, opacity: 0.95 }).addTo(routes);
    else L.circleMarker(segment[0], { radius: 4, color: "#147d78" }).addTo(routes);
  }
  for (const segment of payload.previous ?? []) {
    if (segment.length > 1) L.polyline(segment, { color: "#526574", weight: 3, dashArray: "7 6" }).addTo(previous);
  }
  const all = payload.segments.flat();
  const label = (text) => { const p = document.createElement("div"); p.textContent = text; return p; };
  L.circleMarker(all[0], { radius: 7, color: "#147d78", fillColor: "white", fillOpacity: 1, weight: 3 }).bindPopup(label("開始")).addTo(markers);
  L.circleMarker(all.at(-1), { radius: 7, color: "#192c3b", fillOpacity: 1 }).bindPopup(label("終了")).addTo(markers);
  for (const photo of payload.photos ?? []) {
    const content = label(photo.caption + (photo.note ? "\n" + photo.note : ""));
    const image = document.createElement("img");
    image.src = "https://walkphotos.local/" + encodeURIComponent(photo.image);
    image.alt = photo.note || "撮影した写真";
    content.appendChild(image);
    L.circleMarker([photo.lat, photo.lon], { radius: 6, color: "#885311", fillColor: "#fff3d7", fillOpacity: 1 })
      .bindPopup(content).addTo(markers);
  }
  for (const place of payload.places ?? []) {
    L.circleMarker([place.lat, place.lon], { radius: 3, color: "#526574" }).bindPopup(label(place.name)).addTo(markers);
  }
  const bounds = L.latLngBounds(all);
  map.fitBounds(bounds, { padding: [32, 32], maxZoom: 16, animate: false });
  map.invalidateSize();
}

window.chrome.webview.addEventListener("message", event => {
  try {
    render(event.data);
    window.chrome.webview.postMessage({ kind: "rendered" });
  } catch (error) {
    window.chrome.webview.postMessage({ kind: "error", message: error.message });
  }
});
window.chrome.webview.postMessage({ kind: "ready" });
