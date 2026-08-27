#!/usr/bin/env bash
# Kontrolní dotazy nad produkční databází po importu z DPS plánů.
set -euo pipefail

CONN=$(sed -n 's/^ConnectionStrings__Default=//p' /etc/acs/acs.env)
H=$(sed 's/.*Server=\([^,;]*\).*/\1/' <<<"$CONN")
U=$(sed 's/.*User=\([^;]*\).*/\1/' <<<"$CONN")
P=$(sed 's/.*Password=\([^;]*\).*/\1/' <<<"$CONN")

mysql -h "$H" -u "$U" -p"$P" winpak <<'SQL'
SELECT '--- ukázka čteček ACS.42 ---' AS '';
SELECT r.Name AS ctecka, ro.Name AS mistnost, f.Name AS patro
  FROM Readers r JOIN Rooms ro ON ro.Id = r.RoomId JOIN Floors f ON f.Id = ro.FloorId
  WHERE r.Name LIKE 'ACS.42%' LIMIT 5;

SELECT '--- ukázka chodeb ---' AS '';
SELECT Name AS chodba FROM Corridors LIMIT 4;

SELECT '--- souhrn ---' AS '';
SELECT
  (SELECT COUNT(*) FROM Readers WHERE CorridorId IS NOT NULL) AS ctecky_v_chodbach,
  (SELECT COUNT(*) FROM Readers WHERE RoomId IS NOT NULL) AS ctecky_v_mistnostech,
  (SELECT COUNT(*) FROM Floors f
     WHERE NOT EXISTS(SELECT 1 FROM Rooms WHERE FloorId = f.Id)
       AND NOT EXISTS(SELECT 1 FROM Corridors WHERE FloorId = f.Id)) AS prazdna_patra;
SQL
