#!/usr/bin/env python3
"""
S2G Run Remote Client for Linux/macOS
Polls the HttpListener proxy for commands and executes them on the local machine.
Supports custom reports, Base64-encoded commands, and multi-disk metrics.

No external dependencies - uses only Python standard library.
"""

import os
import sys
import json
import time
import socket
import subprocess
import platform
import base64
import urllib.request
import urllib.error
import urllib.parse
import shutil
from datetime import datetime
from pathlib import Path
from typing import Optional, Dict, Any, List, Tuple

# ============================================
# CONFIGURATION - EDIT THESE VALUES
# ============================================
PROXY_URL = "__PLACEHOLDER_PROXY_URL__"
LISTENER_NODE_ID = "__PLACEHOLDER_LISTENER_ID__"  # Set your HttpListener node ID here
CLIENT_ID = "__PLACEHOLDER_CLIENT_ID__"         # Set your Remote node ClientId here

# Polling intervals
IDLE_POLL_INTERVAL = 60      # Seconds between polls in idle mode
ACTIVE_POLL_INTERVAL = 15    # Seconds between polls in active mode
ACTIVE_DURATION = 60         # Seconds to stay in active mode after command execution
DEFAULT_TIMEOUT = 60         # Default command timeout in seconds
HEARTBEAT_INTERVAL = 30      # Seconds between heartbeats

# Custom Reports
SCRIPT_DIR = Path(__file__).parent.resolve()
REPORTS_DIR = SCRIPT_DIR / "modal_reports"
REPORTS_CONFIG_FILE = REPORTS_DIR / "_config.json"


# ============================================
# HTTP HELPERS (no requests library)
# ============================================

def http_post(url: str, params: Dict[str, str], json_body: Dict, timeout: int = 30) -> Dict:
    """Make HTTP POST request with JSON body using only standard library."""
    # Build URL with query params
    if params:
        query = urllib.parse.urlencode(params)
        full_url = f"{url}?{query}"
    else:
        full_url = url
    
    # Encode JSON body
    data = json.dumps(json_body).encode("utf-8")
    
    # Create request
    req = urllib.request.Request(
        full_url,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    
    # Execute request
    with urllib.request.urlopen(req, timeout=timeout) as response:
        response_data = response.read().decode("utf-8")
        if response_data:
            return json.loads(response_data)
        return {}


# ============================================
# SYSTEM METRICS (no psutil)
# ============================================

def get_cpu_percent() -> float:
    """Get CPU usage percentage using /proc/stat on Linux or fallback."""
    try:
        if platform.system() == "Linux":
            # Read /proc/stat twice with delay
            with open("/proc/stat", "r") as f:
                line1 = f.readline()
            time.sleep(0.1)
            with open("/proc/stat", "r") as f:
                line2 = f.readline()
            
            def parse_cpu_line(line):
                parts = line.split()[1:]  # Skip 'cpu' prefix
                return [int(x) for x in parts]
            
            cpu1 = parse_cpu_line(line1)
            cpu2 = parse_cpu_line(line2)
            
            # Calculate delta
            idle1 = cpu1[3] if len(cpu1) > 3 else 0
            idle2 = cpu2[3] if len(cpu2) > 3 else 0
            total1 = sum(cpu1)
            total2 = sum(cpu2)
            
            idle_delta = idle2 - idle1
            total_delta = total2 - total1
            
            if total_delta > 0:
                return round((1 - idle_delta / total_delta) * 100, 1)
        
        # macOS fallback - use vm_stat and top
        elif platform.system() == "Darwin":
            result = subprocess.run(
                ["top", "-l", "1", "-n", "0"],
                capture_output=True, text=True, timeout=5
            )
            for line in result.stdout.split("\n"):
                if "CPU usage" in line:
                    # Parse: "CPU usage: 5.26% user, 10.52% sys, 84.21% idle"
                    parts = line.split(",")
                    for part in parts:
                        if "idle" in part:
                            idle = float(part.split("%")[0].strip().split()[-1])
                            return round(100 - idle, 1)
    except Exception:
        pass
    
    return 0.0


def get_memory_percent() -> float:
    """Get memory usage percentage using /proc/meminfo on Linux or fallback."""
    try:
        if platform.system() == "Linux":
            with open("/proc/meminfo", "r") as f:
                meminfo = {}
                for line in f:
                    parts = line.split(":")
                    if len(parts) == 2:
                        key = parts[0].strip()
                        value = int(parts[1].strip().split()[0])  # Get kB value
                        meminfo[key] = value
                
                total = meminfo.get("MemTotal", 0)
                available = meminfo.get("MemAvailable", meminfo.get("MemFree", 0))
                
                if total > 0:
                    used = total - available
                    return round((used / total) * 100, 1)
        
        # macOS fallback
        elif platform.system() == "Darwin":
            result = subprocess.run(
                ["vm_stat"],
                capture_output=True, text=True, timeout=5
            )
            page_size = 4096  # Default page size
            stats = {}
            for line in result.stdout.split("\n"):
                if ":" in line:
                    parts = line.split(":")
                    key = parts[0].strip()
                    value = parts[1].strip().rstrip(".")
                    try:
                        stats[key] = int(value)
                    except ValueError:
                        pass
            
            # Calculate used memory
            pages_free = stats.get("Pages free", 0)
            pages_active = stats.get("Pages active", 0)
            pages_inactive = stats.get("Pages inactive", 0)
            pages_wired = stats.get("Pages wired down", 0)
            
            total_pages = pages_free + pages_active + pages_inactive + pages_wired
            if total_pages > 0:
                used_pages = pages_active + pages_wired
                return round((used_pages / total_pages) * 100, 1)
    except Exception:
        pass
    
    return 0.0


def get_disk_metrics() -> Tuple[float, List[Dict]]:
    """Get disk usage metrics for all mounted filesystems."""
    disk_breakdown = []
    total_used = 0
    total_size = 0
    
    try:
        # Get list of mount points
        if platform.system() == "Linux":
            with open("/proc/mounts", "r") as f:
                mounts = []
                for line in f:
                    parts = line.split()
                    if len(parts) >= 2:
                        device = parts[0]
                        mountpoint = parts[1]
                        fstype = parts[2] if len(parts) > 2 else ""
                        # Filter out virtual filesystems
                        if device.startswith("/dev/") and fstype not in ["tmpfs", "devtmpfs", "squashfs"]:
                            mounts.append(mountpoint)
        else:
            # macOS and others - use df command
            result = subprocess.run(
                ["df", "-P"],
                capture_output=True, text=True, timeout=5
            )
            mounts = []
            for line in result.stdout.split("\n")[1:]:  # Skip header
                parts = line.split()
                if len(parts) >= 6 and parts[0].startswith("/"):
                    mounts.append(parts[5])  # Mount point is last column
        
        # Get usage for each mount point using shutil (standard library)
        seen_devices = set()
        for mount in mounts:
            try:
                usage = shutil.disk_usage(mount)
                if usage.total > 0:
                    # Avoid duplicates from bind mounts
                    if usage.total in seen_devices:
                        continue
                    seen_devices.add(usage.total)
                    
                    used_percent = round((usage.used / usage.total) * 100, 1)
                    disk_breakdown.append({
                        "drive": mount,
                        "usedPercent": used_percent,
                        "usedGB": round(usage.used / (1024**3), 1),
                        "freeGB": round(usage.free / (1024**3), 1),
                        "totalGB": round(usage.total / (1024**3), 1)
                    })
                    total_used += usage.used
                    total_size += usage.total
            except (PermissionError, OSError):
                continue
    except Exception:
        # Fallback - just get root filesystem
        try:
            usage = shutil.disk_usage("/")
            total_used = usage.used
            total_size = usage.total
            disk_breakdown.append({
                "drive": "/",
                "usedPercent": round((usage.used / usage.total) * 100, 1),
                "usedGB": round(usage.used / (1024**3), 1),
                "freeGB": round(usage.free / (1024**3), 1),
                "totalGB": round(usage.total / (1024**3), 1)
            })
        except Exception:
            pass
    
    disk_percent = round((total_used / total_size) * 100, 1) if total_size > 0 else 0
    return disk_percent, disk_breakdown


def get_system_metrics() -> Dict[str, Any]:
    """Collect system metrics for reporting, including multi-disk breakdown."""
    disk_percent, disk_breakdown = get_disk_metrics()
    
    return {
        "hostname": socket.gethostname(),
        "os": f"{platform.system()} {platform.release()}",
        "cpu": get_cpu_percent(),
        "memory": get_memory_percent(),
        "disk": disk_percent,
        "diskBreakdown": json.dumps(disk_breakdown),
    }


# ============================================
# CUSTOM REPORTS
# ============================================

def initialize_reports_directory():
    """Ensure the modal_reports directory and config exist."""
    if not REPORTS_DIR.exists():
        REPORTS_DIR.mkdir(parents=True)
        print(f"[{datetime.now().strftime('%H:%M:%S')}] 📁 Created modal_reports directory")
    
    if not REPORTS_CONFIG_FILE.exists():
        REPORTS_CONFIG_FILE.write_text(json.dumps({"reports": []}))


def get_reports_config() -> Dict:
    """Load the reports configuration."""
    try:
        if REPORTS_CONFIG_FILE.exists():
            return json.loads(REPORTS_CONFIG_FILE.read_text())
    except Exception as e:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] ⚠️ Error loading reports config: {e}")
    return {"reports": []}


def save_reports_config(config: Dict):
    """Save the reports configuration."""
    try:
        REPORTS_CONFIG_FILE.write_text(json.dumps(config, indent=2))
    except Exception as e:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] ⚠️ Error saving reports config: {e}")


def invoke_custom_reports() -> List[Dict]:
    """Execute all enabled custom reports and return results."""
    results = []
    config = get_reports_config()
    now = datetime.now().isoformat()
    
    for report in config.get("reports", []):
        if not report.get("enabled", False):
            continue
        
        report_name = report.get("name", "unknown")
        script_path = REPORTS_DIR / f"{report_name}.sh"
        
        if not script_path.exists():
            results.append({
                "name": report_name,
                "enabled": False,
                "error": f"Script file not found: {report_name}.sh",
                "data": None,
                "lastUpdated": now
            })
            continue
        
        try:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] 📊 Executing report: {report_name}")
            
            # Execute as shell script (bash on Linux/macOS)
            result = subprocess.run(
                ["/bin/sh", str(script_path)],
                capture_output=True,
                text=True,
                timeout=30
            )
            
            output = result.stdout.strip()
            
            # Try to parse as JSON
            try:
                data = json.loads(output)
            except json.JSONDecodeError:
                data = output
            
            results.append({
                "name": report_name,
                "enabled": True,
                "error": None,
                "data": data,
                "lastUpdated": now
            })
            print(f"[{datetime.now().strftime('%H:%M:%S')}] ✅ Report completed: {report_name}")
            
        except subprocess.TimeoutExpired:
            results.append({
                "name": report_name,
                "enabled": True,
                "error": "Report timed out after 30 seconds",
                "data": None,
                "lastUpdated": now
            })
        except Exception as e:
            results.append({
                "name": report_name,
                "enabled": True,
                "error": str(e),
                "data": None,
                "lastUpdated": now
            })
    
    return results


def handle_report_command(report_cmd: Dict):
    """Handle report management commands from server (add/update/delete/toggle)."""
    action = report_cmd.get("action")
    report_name = report_cmd.get("name")
    script_content = report_cmd.get("script", "")
    interval_seconds = report_cmd.get("intervalSeconds", 60)
    
    config = get_reports_config()
    script_path = REPORTS_DIR / f"{report_name}.sh"
    
    if action == "add":
        script_path.write_text(script_content)
        
        # Update config
        existing = next((r for r in config["reports"] if r["name"] == report_name), None)
        if existing:
            existing["intervalSeconds"] = interval_seconds
            existing["enabled"] = True
        else:
            config["reports"].append({
                "name": report_name,
                "enabled": True,
                "intervalSeconds": interval_seconds
            })
        save_reports_config(config)
        print(f"[{datetime.now().strftime('%H:%M:%S')}] ➕ Added report: {report_name}")
        
    elif action == "update":
        if script_content:
            script_path.write_text(script_content)
        existing = next((r for r in config["reports"] if r["name"] == report_name), None)
        if existing and interval_seconds:
            existing["intervalSeconds"] = interval_seconds
        save_reports_config(config)
        print(f"[{datetime.now().strftime('%H:%M:%S')}] ✏️ Updated report: {report_name}")
        
    elif action == "delete":
        if script_path.exists():
            script_path.unlink()
        config["reports"] = [r for r in config["reports"] if r["name"] != report_name]
        save_reports_config(config)
        print(f"[{datetime.now().strftime('%H:%M:%S')}] 🗑️ Deleted report: {report_name}")
        
    elif action == "toggle":
        existing = next((r for r in config["reports"] if r["name"] == report_name), None)
        if existing:
            existing["enabled"] = not existing.get("enabled", False)
            save_reports_config(config)
            print(f"[{datetime.now().strftime('%H:%M:%S')}] 🔄 Toggled report: {report_name} (enabled={existing['enabled']})")


# ============================================
# COMMAND EXECUTION
# ============================================

def fetch_commands() -> List[Dict]:
    """Fetch pending commands from the Remote node via HttpListener proxy."""
    try:
        data = http_post(
            PROXY_URL,
            params={"nodeId": LISTENER_NODE_ID},
            json_body={"action": "fetch", "clientId": CLIENT_ID},
            timeout=30
        )
        
        # Handle various response formats
        if isinstance(data, list):
            return data
        elif isinstance(data, dict):
            if "body" in data:
                body = data["body"]
                if isinstance(body, str):
                    return json.loads(body)
                return body if isinstance(body, list) else []
            elif "executionId" in data and "command" in data:
                return [data]
        return []
        
    except Exception as e:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] Fetch error: {e}")
        return []


def execute_command(command: str, timeout: int = DEFAULT_TIMEOUT) -> Tuple[str, int]:
    """
    Execute a shell command and return (output, exit_code).
    Supports Base64-encoded commands with __PS64__ prefix (decoded as shell script).
    """
    try:
        actual_command = command
        
        # Check if this is a Base64-encoded command
        if command.startswith("__PS64__"):
            base64_part = command[8:]  # Remove __PS64__ prefix
            actual_command = base64.b64decode(base64_part).decode("utf-8")
            print(f"[{datetime.now().strftime('%H:%M:%S')}] 🔓 Decoded command: {actual_command[:80]}...")
        
        result = subprocess.run(
            actual_command,
            shell=True,
            capture_output=True,
            text=True,
            timeout=timeout
        )
        
        output = result.stdout
        if result.stderr:
            if output:
                output += "\n\n--- STDERR ---\n"
            output += result.stderr
        
        return output.strip(), result.returncode
        
    except subprocess.TimeoutExpired:
        return f"⏱️ Command timed out after {timeout} seconds", -1
    except Exception as e:
        return f"❌ Execution error: {str(e)}", -1


def submit_output(execution_id: str, output: str, exit_code: int, metrics: Dict[str, Any]):
    """Submit command output and system metrics to the Remote node."""
    try:
        payload = {
            "action": "submit",
            "clientId": CLIENT_ID,
            "executionId": execution_id,
            "output": output[:50000],  # Limit output size
            "exitCode": exit_code,
            "hostname": metrics["hostname"],
            "os": metrics["os"],
            "cpu": metrics["cpu"],
            "memory": metrics["memory"],
            "disk": metrics["disk"],
        }
        
        http_post(
            PROXY_URL,
            params={"nodeId": LISTENER_NODE_ID},
            json_body=payload,
            timeout=30
        )
        print(f"[{datetime.now().strftime('%H:%M:%S')}] ✅ Submitted result for {execution_id[:8]}...")
        
    except Exception as e:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] Submit error: {e}")


def send_heartbeat(metrics: Dict[str, Any]):
    """Send heartbeat with system metrics and custom reports."""
    try:
        # Execute custom reports
        custom_reports = invoke_custom_reports()
        reports_json = json.dumps(custom_reports) if custom_reports else "[]"
        
        payload = {
            "action": "heartbeat",
            "clientId": CLIENT_ID,
            "hostname": metrics["hostname"],
            "os": metrics["os"],
            "cpu": metrics["cpu"],
            "memory": metrics["memory"],
            "disk": metrics["disk"],
            "diskBreakdown": metrics["diskBreakdown"],
            "customReports": reports_json,
        }
        
        http_post(
            PROXY_URL,
            params={"nodeId": LISTENER_NODE_ID},
            json_body=payload,
            timeout=10
        )
        
        report_count = len([r for r in custom_reports if r])
        report_info = f", Reports: {report_count}" if report_count > 0 else ""
        print(f"[{datetime.now().strftime('%H:%M:%S')}] 💓 Heartbeat sent (CPU: {metrics['cpu']:.1f}%, Mem: {metrics['memory']:.1f}%, Disk: {metrics['disk']:.1f}%{report_info})")
        
    except Exception as e:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] Heartbeat error: {e}")


# ============================================
# MAIN LOOP
# ============================================

def main():
    """Main polling loop."""
    if not LISTENER_NODE_ID or not CLIENT_ID:
        print("ERROR: Please configure LISTENER_NODE_ID and CLIENT_ID at the top of this script.")
        print("  LISTENER_NODE_ID = Your HttpListener node ID from the workflow")
        print("  CLIENT_ID = Your Remote node ClientId from the node properties")
        sys.exit(1)
    
    print("S2G Run Remote Client starting...")
    print(f"  Listener Node: {LISTENER_NODE_ID[:8]}...")
    print(f"  Client ID: {CLIENT_ID[:8]}...")
    print(f"  Hostname: {socket.gethostname()}")
    print(f"  OS: {platform.system()} {platform.release()}")
    print()
    
    # Initialize custom reports directory
    initialize_reports_directory()
    reports_config = get_reports_config()
    enabled_reports = len([r for r in reports_config.get("reports", []) if r.get("enabled")])
    if enabled_reports > 0:
        print(f"  📊 Custom Reports: {enabled_reports} enabled")
    print()
    
    last_command_time: Optional[datetime] = None
    last_heartbeat: Optional[datetime] = None
    
    while True:
        try:
            # Send heartbeat at configured interval
            if last_heartbeat is None or (datetime.now() - last_heartbeat).total_seconds() >= HEARTBEAT_INTERVAL:
                metrics = get_system_metrics()
                send_heartbeat(metrics)
                last_heartbeat = datetime.now()
            
            # Determine poll interval based on mode
            if last_command_time and (datetime.now() - last_command_time).total_seconds() < ACTIVE_DURATION:
                poll_interval = ACTIVE_POLL_INTERVAL
                mode = "ACTIVE"
            else:
                poll_interval = IDLE_POLL_INTERVAL
                mode = "IDLE"
            
            print(f"[{datetime.now().strftime('%H:%M:%S')}] [{mode}] Polling for commands...")
            
            commands = fetch_commands()
            
            if commands:
                print(f"[{datetime.now().strftime('%H:%M:%S')}] Received {len(commands)} command(s)")
                last_command_time = datetime.now()
                
                for cmd in commands:
                    execution_id = cmd.get("executionId", "unknown")
                    command = cmd.get("command", "")
                    timeout = cmd.get("timeoutSeconds", DEFAULT_TIMEOUT)
                    
                    # Check if this is a report management command
                    if command and command.startswith("__REPORT__"):
                        try:
                            report_json = command[10:]  # Remove __REPORT__ prefix
                            report_cmd = json.loads(report_json)
                            handle_report_command(report_cmd)
                            
                            metrics = get_system_metrics()
                            submit_output(execution_id, "Report command executed successfully", 0, metrics)
                        except Exception as e:
                            metrics = get_system_metrics()
                            submit_output(execution_id, f"Report command error: {e}", 1, metrics)
                        continue
                    
                    print(f"[{datetime.now().strftime('%H:%M:%S')}] Executing: {command[:50]}...")
                    
                    output, exit_code = execute_command(command, timeout)
                    metrics = get_system_metrics()
                    
                    status_icon = "✅" if exit_code == 0 else "❌"
                    print(f"[{datetime.now().strftime('%H:%M:%S')}] {status_icon} Exit code: {exit_code}")
                    
                    submit_output(execution_id, output, exit_code, metrics)
            
            time.sleep(poll_interval)
            
        except KeyboardInterrupt:
            print("\nShutting down...")
            break
        except Exception as e:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] Error: {e}")
            time.sleep(IDLE_POLL_INTERVAL)


if __name__ == "__main__":
    main()
