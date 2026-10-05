const map = L.map('map').setView([35.681236, 139.767125], 14);
const route = L.layerGroup().addTo(map);
const notice = document.getElementById('notice');
let tileFailed = false;
let centered = false;
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
}).addTo(map).on('tileerror', () => {
  tileFailed = true;
  notice.hidden = false;
  notice.textContent = '地図タイルを取得できません。GPS記録は継続します。';
});
window.drawTrack = data => {
  route.clearLayers();
  const points = data.segments.flat();
  for (const segment of data.segments) {
    L.polyline(segment, { color: '#147D78', weight: 5 }).addTo(route);
  }
  if (points.length) {
    L.circleMarker(points[points.length - 1], { radius: 7, color: '#147D78', fillOpacity: 1 }).addTo(route);
    if (!centered) { map.setView(points[0], 16); centered = true; }
    notice.hidden = !tileFailed;
  } else {
    centered = false;
    notice.hidden = false;
    notice.textContent = 'GPS未取得・地図は初期表示';
  }
  for (const photo of data.photos) {
    L.circleMarker([photo.Lat, photo.Lon], { radius: 5, color: '#855E34', fillOpacity: 1 }).addTo(route);
  }
  map.invalidateSize();
};
