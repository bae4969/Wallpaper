import csv
import json
import shutil
import subprocess
import sys
import time

interval = max(1, min(60, int(sys.argv[1])))
include_gpu = sys.argv[2] == '1'
last_cpu = None
last_net = {}
last_time = None


def number(value):
    try:
        return float(value)
    except ValueError:
        return None


def read_sample():
    global last_cpu, last_net, last_time
    with open('/proc/stat') as source:
        ticks = [int(value) for value in source.readline().split()[1:9]]
    total, idle = sum(ticks), ticks[3] + ticks[4]
    cpu = None
    if last_cpu and total > last_cpu[0]:
        cpu = max(0, min(100, 100 * (1 - (idle - last_cpu[1]) / (total - last_cpu[0]))))
    last_cpu = (total, idle)

    with open('/proc/meminfo') as source:
        memory = {line.split(':')[0]: int(line.split()[1]) * 1024 for line in source}
    memory_total = memory['MemTotal']
    available = memory.get('MemAvailable', memory.get('MemFree', 0) + memory.get('Buffers', 0) + memory.get('Cached', 0))
    memory_used = max(0, memory_total - available)

    routes = set()
    with open('/proc/net/route') as source:
        for line in list(source)[1:]:
            parts = line.split()
            if len(parts) > 3 and parts[1] == '00000000' and int(parts[3], 16) & 1:
                routes.add(parts[0])
    network = {}
    with open('/proc/net/dev') as source:
        for line in list(source)[2:]:
            name, values = line.split(':', 1)
            name, values = name.strip(), values.split()
            if name != 'lo':
                network[name] = (int(values[0]), int(values[8]))
    selected = routes.intersection(network) or set(sorted(network)[:1])
    now = time.monotonic()
    download = upload = None
    previous = [name for name in selected if name in last_net and all(a >= b for a, b in zip(network[name], last_net[name]))]
    if previous and last_time is not None and now > last_time:
        download = sum(network[name][0] - last_net[name][0] for name in previous) / (now - last_time)
        upload = sum(network[name][1] - last_net[name][1] for name in previous) / (now - last_time)
    last_net, last_time = network, now

    gpu, gpu_error = [], None
    if include_gpu:
        if not shutil.which('nvidia-smi'):
            gpu_error = 'NVIDIA GPU 정보 없음'
        else:
            try:
                result = subprocess.run(['nvidia-smi', '--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw', '--format=csv,noheader,nounits'],
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True, timeout=2.5)
                if result.returncode:
                    gpu_error = 'GPU 정보 읽기 실패'
                else:
                    for fields in csv.reader(result.stdout.splitlines(), skipinitialspace=True):
                        if len(fields) >= 7:
                            gpu.append(dict(index=int(fields[0]), name=fields[1], utilization=number(fields[2]),
                                            memoryUsed=number(fields[3]), memoryTotal=number(fields[4]),
                                            temperature=number(fields[5]), power=number(fields[6])))
            except (OSError, ValueError, subprocess.TimeoutExpired):
                gpu_error = 'GPU 정보 읽기 실패'
    return dict(cpu=cpu, memoryPercent=100 * memory_used / memory_total, memoryUsed=memory_used,
                memoryTotal=memory_total, download=download, upload=upload, gpu=gpu, gpuError=gpu_error)


while True:
    started = time.monotonic()
    print(json.dumps(read_sample(), ensure_ascii=True, allow_nan=False), flush=True)
    time.sleep(max(0, interval - (time.monotonic() - started)))
