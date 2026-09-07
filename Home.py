import os
import csv
import math

# ==============================================================================
# 1. МАТРИЧНАЯ АЛГЕБРА И ТРАНСФОРМАЦИИ (СОГЛАСОВАНО С SYSTEM.NUMERICS)
# ==============================================================================

def get_rotation_matrix(yaw_deg: float, pitch_deg: float, roll_deg: float):
    """
    Создает матрицу вращения 3x3 (Yaw -> Pitch -> Roll), 
    полностью совместимую с порядком System.Numerics.Matrix4x4.CreateFromYawPitchRoll.
    Порядок: Z (Roll), затем X (Pitch), затем Y (Yaw).
    """
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    roll = math.radians(roll_deg)
    
    cy, sy = math.cos(yaw), math.sin(yaw)
    Ry = [
        [ cy, 0.0,  sy],
        [0.0, 1.0, 0.0],
        [-sy, 0.0,  cy]
    ]
    
    cp, sp = math.cos(pitch), math.sin(pitch)
    Rx = [
        [1.0, 0.0,  0.0],
        [0.0,  cp,  -sp],
        [0.0,  sp,   cp]
    ]
    
    cr, sr = math.cos(roll), math.sin(roll)
    Rz = [
        [ cr, -sr, 0.0],
        [ sr,  cr, 0.0],
        [0.0, 0.0, 1.0]
    ]
    
    def mat_mult(A, B):
        return [
            [sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)]
            for i in range(3)
        ]
        
    return mat_mult(mat_mult(Ry, Rx), Rz)

def mat_transpose(M):
    """Обратное вращение (ортогональная матрица)."""
    return [[M[j][i] for j in range(3)] for i in range(3)]

def mat_vec_mult(M, v):
    """Умножение матрицы на трехмерный вектор."""
    return (
        M[0][0]*v[0] + M[0][1]*v[1] + M[0][2]*v[2],
        M[1][0]*v[0] + M[1][1]*v[1] + M[1][2]*v[2],
        M[2][0]*v[0] + M[2][1]*v[1] + M[2][2]*v[2]
    )

# ==============================================================================
# 2. ГЕНЕРАЦИЯ ГЕОМЕТРИИ ОБЪЕКТА
# ==============================================================================

def generate_house_global_points(step: float = 0.08):
    points = []
    min_x, max_x = -5.0, 5.0
    min_z, max_z = -5.0, 5.0
    min_y, max_y = 0.0, 3.2

    def frange(start, stop, step_val):
        curr = start
        while curr <= stop + 1e-7:
            yield round(curr, 5)
            curr += step_val

    # 1. Пол (Y = 0)
    for x in frange(min_x, max_x, step):
        for z in frange(min_z, max_z, step):
            points.append(((x, 0.0, z), (130, 85, 45)))

    # 2. Потолок (Y = 3.2)
    for x in frange(min_x, max_x, step):
        for z in frange(min_z, max_z, step):
            points.append(((x, max_y, z), (230, 230, 230)))

    # 3. Передняя стена (Z = -5.0) с дверью и окнами
    for x in frange(min_x, max_x, step):
        for y in frange(min_y, max_y, step):
            if -0.9 <= x <= 0.9 and y <= 2.1:
                color = (220, 220, 220) if (0.6 <= x <= 0.8 and 0.9 <= y <= 1.1) else (90, 45, 15)
            elif (-4.0 <= x <= -2.0 or 2.0 <= x <= 4.0) and 1.1 <= y <= 2.3:
                is_frame = (x <= -3.8 or x >= -2.2 or x <= 2.2 or x >= 3.8 or y <= 1.2 or y >= 2.2)
                color = (60, 60, 60) if is_frame else (120, 190, 235)
            else:
                color = (230, 220, 200)
            points.append(((x, y, -5.0), color))

    # 4. Задняя стена (Z = 5.0)
    for x in frange(min_x, max_x, step):
        for y in frange(min_y, max_y, step):
            color = (120, 190, 235) if (-1.5 <= x <= 1.5 and 1.1 <= y <= 2.3) else (230, 220, 200)
            points.append(((x, y, 5.0), color))

    # 5. Левая стена (X = -5.0)
    for z in frange(min_z, max_z, step):
        for y in frange(min_y, max_y, step):
            color = (120, 190, 235) if (-1.5 <= z <= 1.5 and 1.1 <= y <= 2.3) else (230, 220, 200)
            points.append(((-5.0, y, z), color))

    # 6. Правая стена (X = 5.0)
    for z in frange(min_z, max_z, step):
        for y in frange(min_y, max_y, step):
            points.append(((5.0, y, z), (230, 220, 200)))

    # 7. Стол в интерьере
    for x in frange(-1.2, 1.2, step):
        for z in frange(-0.8, 0.8, step):
            points.append(((x, 0.75, z), (100, 55, 25)))

    return points

# ==============================================================================
# 3. ЭКСПОРТ СКАНА И ТЕЛЕМЕТРИИ
# ==============================================================================

def export_scan_files(global_points, ply_path, csv_path, drone_pos, rep_pos, yaw_deg, pitch_deg, roll_deg):
    os.makedirs(os.path.dirname(os.path.abspath(csv_path)), exist_ok=True)
    with open(csv_path, mode='w', newline='', encoding='utf-8') as f:
        writer = csv.writer(f)
        writer.writerow(["DroneX", "DroneY", "DroneZ", "RepX", "RepY", "RepZ", "Yaw", "Pitch", "Roll"])
        writer.writerow([
            f"{drone_pos[0]:.4f}", f"{drone_pos[1]:.4f}", f"{drone_pos[2]:.4f}",
            f"{rep_pos[0]:.4f}",   f"{rep_pos[1]:.4f}",   f"{rep_pos[2]:.4f}",
            f"{yaw_deg:.2f}",      f"{pitch_deg:.2f}",    f"{roll_deg:.2f}"
        ])

    R = get_rotation_matrix(yaw_deg, pitch_deg, roll_deg)
    R_inv = mat_transpose(R)
    
    # Вектор взгляда лидара вперед (+Z в локальных координатах дрона)
    forward_dir = (R[0][2], R[1][2], R[2][2])

    local_ply_points = []
    for pos, color in global_points:
        dx = pos[0] - drone_pos[0]
        dy = pos[1] - drone_pos[1]
        dz = pos[2] - drone_pos[2]
        
        dist = math.sqrt(dx*dx + dy*dy + dz*dz)
        if dist == 0.0:
            continue

        dir_norm = (dx / dist, dy / dist, dz / dist)
        dot_product = (dir_norm[0] * forward_dir[0] + 
                       dir_norm[1] * forward_dir[1] + 
                       dir_norm[2] * forward_dir[2])

        # Лидар видит объекты в конусе перед собой (dot_product > 0.1) на дистанции до 15м
        if dist <= 15.0 and dot_product > 0.1:
            lx, ly, lz = mat_vec_mult(R_inv, (dx, dy, dz))
            local_ply_points.append((lx, ly, lz, color[0], color[1], color[2]))

    os.makedirs(os.path.dirname(os.path.abspath(ply_path)), exist_ok=True)
    with open(ply_path, mode='w', encoding='utf-8') as f:
        f.write("ply\nformat ascii 1.0\n")
        f.write(f"element vertex {len(local_ply_points)}\n")
        f.write("property float x\nproperty float y\nproperty float z\n")
        f.write("property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n")
        
        # Буферизованный вывод
        lines = [f"{p[0]:.4f} {p[1]:.4f} {p[2]:.4f} {p[3]} {p[4]} {p[5]}\n" for p in local_ply_points]
        f.writelines(lines)

    print(f"[OK] Сгенерирован скан: {os.path.basename(ply_path)} ({len(local_ply_points)} точек)")

def main():
    print("=== ГЕНЕРАЦИЯ СИНТЕТИЧЕСКИХ СКАНОВ ДЛЯ ТЕСТИРОВАНИЯ ===")
    global_house = generate_house_global_points(step=0.08)
    print(f"[ИНФО] Сформирована мастер-модель: {len(global_house)} точек")

    scans_config = [
        {
            "ply": "scan_zone_1.ply", "csv": "scan_zone_1.csv",
            "drone_pos": (0.0, 1.5, -8.0), "rep_pos": (0.0, 0.0, 0.0),
            "yaw": 0.0, "pitch": 0.0, "roll": 0.0
        },
        {
            "ply": "scan_zone_2.ply", "csv": "scan_zone_2.csv",
            "drone_pos": (6.5, 1.8, -4.0), "rep_pos": (0.0, 0.0, 0.0),
            "yaw": -45.0, "pitch": 0.0, "roll": 0.0
        },
        {
            "ply": "scan_zone_3.ply", "csv": "scan_zone_3.csv",
            "drone_pos": (-2.0, 1.5, 2.0), "rep_pos": (0.0, 0.0, 0.0),
            "yaw": 135.0, "pitch": 0.0, "roll": 0.0
        }
    ]

    for cfg in scans_config:
        export_scan_files(
            global_house, cfg["ply"], cfg["csv"],
            cfg["drone_pos"], cfg["rep_pos"],
            cfg["yaw"], cfg["pitch"], cfg["roll"]
        )

if __name__ == "__main__":
    main()