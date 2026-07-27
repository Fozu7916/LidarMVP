import os
import csv
import math

# ==============================================================================
# 1. ВЕКТОРНАЯ И МАТРИЧНАЯ АЛГЕБРА (ТРЕХМЕРНЫЕ ТРАНСФОРМАЦИИ)
# ==============================================================================

def get_rotation_matrix(yaw_deg: float, pitch_deg: float, roll_deg: float):
    """
    Создает матрицу вращения 3x3 на основе углов Эйлера (Yaw-Pitch-Roll).
    Порядок применения: Yaw (вокруг Y), Pitch (вокруг X), Roll (вокруг Z).
    """
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    roll = math.radians(roll_deg)
    
    # Вращение вокруг оси Y (Yaw)
    cy, sy = math.cos(yaw), math.sin(yaw)
    Ry = [
        [cy,  0.0, sy],
        [0.0, 1.0, 0.0],
        [-sy, 0.0, cy]
    ]
    
    # Вращение вокруг оси X (Pitch)
    cp, sp = math.cos(pitch), math.sin(pitch)
    Rx = [
        [1.0, 0.0, 0.0],
        [0.0, cp,  -sp],
        [0.0, sp,  cp]
    ]
    
    # Вращение вокруг оси Z (Roll)
    cr, sr = math.cos(roll), math.sin(roll)
    Rz = [
        [cr,  -sr, 0.0],
        [sr,  cr,  0.0],
        [0.0, 0.0, 1.0]
    ]
    
    # Перемножение матриц R = Ry * Rx * Rz
    def mat_mult(A, B):
        return [
            [sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)]
            for i in range(3)
        ]
        
    return mat_mult(mat_mult(Ry, Rx), Rz)

def mat_transpose(M):
    """Транспонирование матрицы 3x3 (для обратного вращения)."""
    return [[M[j][i] for j in range(3)] for i in range(3)]

def mat_vec_mult(M, v):
    """Умножение матрицы 3x3 на трехмерный вектор (x, y, z)."""
    return (
        M[0][0]*v[0] + M[0][1]*v[1] + M[0][2]*v[2],
        M[1][0]*v[0] + M[1][1]*v[1] + M[1][2]*v[2],
        M[2][0]*v[0] + M[2][1]*v[1] + M[2][2]*v[2]
    )

# ==============================================================================
# 2. ГЕНЕРАЦИЯ ГЛОБАЛЬНОЙ ГЕОМЕТРИИ ДОМА (10 x 10 x 3.2 м)
# ==============================================================================

def generate_house_global_points(step: float = 0.10):
    """
    Генерирует единый монолитный дом в глобальной системе координат (X, Y, Z).
    Включает:
    - Деревянный пол (паркет)
    - Белый штукатуренный потолок
    - Фасад с темно-дубовой дверью и металлической ручкой
    - Окна с антрацитовыми рамами и синеватым стеклом
    - Деревянный обеденный стол внутри помещения
    """
    points = []
    
    min_x, max_x = -5.0, 5.0
    min_z, max_z = -5.0, 5.0
    min_y, max_y = 0.0, 3.2

    def frange(start, stop, step_val):
        curr = start
        while curr <= stop + 1e-7:
            yield round(curr, 5)
            curr += step_val

    # 1. Пол (Y = 0) — Деревянный паркет (RGB 130, 85, 45)
    for x in frange(min_x, max_x, step):
        for z in frange(min_z, max_z, step):
            points.append(((x, 0.0, z), (130, 85, 45)))

    # 2. Потолок (Y = 3.2) — Белая отделка (RGB 230, 230, 230)
    for x in frange(min_x, max_x, step):
        for z in frange(min_z, max_z, step):
            points.append(((x, max_y, z), (230, 230, 230)))

    # 3. Передняя стена (Z = -5.0) — Дверь, окна и штукатурка
    for x in frange(min_x, max_x, step):
        for y in frange(min_y, max_y, step):
            # Дверь: X от -0.9 до 0.9, Y от 0 до 2.1
            if -0.9 <= x <= 0.9 and y <= 2.1:
                # Дверная ручка (металлический цвет)
                if 0.6 <= x <= 0.8 and 0.9 <= y <= 1.1:
                    color = (220, 220, 220)
                else:
                    color = (90, 45, 15)  # Темный дуб
            # Левое окно: X от -4.0 до -2.0, Y от 1.1 до 2.3
            elif -4.0 <= x <= -2.0 and 1.1 <= y <= 2.3:
                if x <= -3.8 or x >= -2.2 or y <= 1.2 or y >= 2.2:
                    color = (60, 60, 60)   # Антрацитовая рама
                else:
                    color = (120, 190, 235)  # Стекло
            # Правое окно: X от 2.0 до 4.0, Y от 1.1 до 2.3
            elif 2.0 <= x <= 4.0 and 1.1 <= y <= 2.3:
                if x <= 2.2 or x >= 3.8 or y <= 1.2 or y >= 2.2:
                    color = (60, 60, 60)   # Антрацитовая рама
                else:
                    color = (120, 190, 235)  # Стекло
            else:
                color = (230, 220, 200)  # Бежевая штукатурка
            points.append(((x, y, -5.0), color))

    # 4. Задняя стена (Z = 5.0) с центральным окном
    for x in frange(min_x, max_x, step):
        for y in frange(min_y, max_y, step):
            if -1.5 <= x <= 1.5 and 1.1 <= y <= 2.3:
                color = (120, 190, 235)
            else:
                color = (230, 220, 200)
            points.append(((x, y, 5.0), color))

    # 5. Левая стена (X = -5.0) с боковым окном
    for z in frange(min_z, max_z, step):
        for y in frange(min_y, max_y, step):
            if -1.5 <= z <= 1.5 and 1.1 <= y <= 2.3:
                color = (120, 190, 235)
            else:
                color = (230, 220, 200)
            points.append(((-5.0, y, z), color))

    # 6. Правая стена (X = 5.0)
    for z in frange(min_z, max_z, step):
        for y in frange(min_y, max_y, step):
            points.append(((5.0, y, z), (230, 220, 200)))

    # 7. Интерьер: Стол внутри комнаты (X [-1.2, 1.2], Z [-0.8, 0.8], Y = 0.75)
    for x in frange(-1.2, 1.2, step):
        for z in frange(-0.8, 0.8, step):
            points.append(((x, 0.75, z), (100, 55, 25)))

    return points

# ==============================================================================
# 3. ЭКСПОРТ И ИМИТАЦИЯ СКАНИРОВАНИЯ ДРОНА (СИМУЛЯЦИЯ ЛИДАРА)
# ==============================================================================

def export_scan_files(global_points, ply_path, csv_path, drone_pos, rep_pos, yaw_deg, pitch_deg, roll_deg):
    """
    1. Записывает CSV-файл телеметрии с координатами и углами ориентации.
    2. Пересчитывает глобальные координаты дома в ЛОКАЛЬНУЮ систему координат сканера.
    3. Отсекает точки вне зоны видимости лидара (по дальности и углу обзора).
    4. Записывает итоговый ASCII PLY-файл.
    """
    # 1. Сохранение CSV телеметрии
    os.makedirs(os.path.dirname(os.path.abspath(csv_path)), exist_ok=True)
    with open(csv_path, mode='w', newline='', encoding='utf-8') as f:
        writer = csv.writer(f)
        writer.writerow(["DroneX", "DroneY", "DroneZ", "RepX", "RepY", "RepZ", "Yaw", "Pitch", "Roll"])
        writer.writerow([
            f"{drone_pos[0]:.4f}", f"{drone_pos[1]:.4f}", f"{drone_pos[2]:.4f}",
            f"{rep_pos[0]:.4f}",   f"{rep_pos[1]:.4f}",   f"{rep_pos[2]:.4f}",
            f"{yaw_deg:.2f}",      f"{pitch_deg:.2f}",    f"{roll_deg:.2f}"
        ])

    # 2. Матрица вращения и прямой вектор взгляда сканера
    R = get_rotation_matrix(yaw_deg, pitch_deg, roll_deg)
    R_inv = mat_transpose(R)  # Для перехода "Мир -> Локальные координаты"
    
    # Вектор "вперед" сканера в мировой системе координат (направление +Z)
    forward_dir = (R[0][2], R[1][2], R[2][2])

    local_ply_points = []

    # 3. Трансформация и фильтрация видимости
    for pos, color in global_points:
        dx = pos[0] - drone_pos[0]
        dy = pos[1] - drone_pos[1]
        dz = pos[2] - drone_pos[2]
        
        dist = math.sqrt(dx*dx + dy*dy + dz*dz)
        if dist == 0.0:
            continue

        # Скалярное произведение для определения попадания точки в переднее полушарие сканера
        dir_norm = (dx / dist, dy / dist, dz / dist)
        dot_product = (dir_norm[0] * forward_dir[0] + 
                       dir_norm[1] * forward_dir[1] + 
                       dir_norm[2] * forward_dir[2])

        # Лидар фиксирует объекты на расстоянии до 12 м под конусом обзора
        if dist <= 12.0 and dot_product > -0.3:
            # Трансформация: P_local = R^(-1) * (P_global - P_drone)
            lx, ly, lz = mat_vec_mult(R_inv, (dx, dy, dz))
            local_ply_points.append((lx, ly, lz, color[0], color[1], color[2]))

    # 4. Сохранение ASCII PLY файла
    os.makedirs(os.path.dirname(os.path.abspath(ply_path)), exist_ok=True)
    with open(ply_path, mode='w', encoding='utf-8') as f:
        f.write("ply\n")
        f.write("format ascii 1.0\n")
        f.write(f"element vertex {len(local_ply_points)}\n")
        f.write("property float x\n")
        f.write("property float y\n")
        f.write("property float z\n")
        f.write("property uchar red\n")
        f.write("property uchar green\n")
        f.write("property uchar blue\n")
        f.write("end_header\n")
        
        for p in local_ply_points:
            f.write(f"{p[0]:.4f} {p[1]:.4f} {p[2]:.4f} {p[3]} {p[4]} {p[5]}\n")

    print(f"[OK] Сгенерирован скан: {os.path.basename(ply_path)} ({len(local_ply_points)} точек) и {os.path.basename(csv_path)}")

# ==============================================================================
# 4. ТОЧКА ВХОДА (ОПИСАНИЕ МИССИИ И 3 РАКУРСОВ)
# ==============================================================================

def main():
    print("=== ГЕНЕРАЦИЯ СИНТЕТИЧЕСКИХ СНЕМКОВ ЛИДАРА (1 ДОМ, 3 РАКУРСА) ===")
    
    step_size = 0.10  # Шаг сетки (10 см)
    global_house = generate_house_global_points(step=step_size)
    print(f"[ИНФО] Сформирована геометрия дома. Всего точек в мастер-модели: {len(global_house)}")

    # Конфигурация 3 сканов (позиции дрона, репитера и углы Yaw, Pitch, Roll)
    scans_config = [
        {
            "ply": "scan_zone_1.ply",
            "csv": "scan_zone_1.csv",
            "drone_pos": (0.0, 1.5, -8.0),   # Перед фасадом и дверью
            "rep_pos": (0.0, 0.0, 0.0),
            "yaw": 0.0, "pitch": 0.0, "roll": 0.0
        },
        {
            "ply": "scan_zone_2.ply",
            "csv": "scan_zone_2.csv",
            "drone_pos": (6.0, 1.8, -4.0),   # Угловая съемка справа
            "rep_pos": (0.0, 0.0, 0.0),
            "yaw": -50.0, "pitch": 0.0, "roll": 0.0
        },
        {
            "ply": "scan_zone_3.ply",
            "csv": "scan_zone_3.csv",
            "drone_pos": (-2.0, 1.5, 2.0),   # Съемка интерьера/стола изнутри
            "rep_pos": (0.0, 0.0, 0.0),
            "yaw": 140.0, "pitch": 0.0, "roll": 0.0
        }
    ]

    for cfg in scans_config:
        export_scan_files(
            global_points=global_house,
            ply_path=cfg["ply"],
            csv_path=cfg["csv"],
            drone_pos=cfg["drone_pos"],
            rep_pos=cfg["rep_pos"],
            yaw_deg=cfg["yaw"],
            pitch_deg=cfg["pitch"],
            roll_deg=cfg["roll"]
        )

    print("\n[УСПЕХ] Все файлы созданы в текущей директории.")

if __name__ == "__main__":
    main()