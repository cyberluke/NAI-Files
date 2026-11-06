"""Exercise Writer's real .uno:Open -> native picker -> document-load path, without UI automation."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import uuid
import winreg

import uno
from com.sun.star.beans import PropertyValue
from com.sun.star.lang import DisposedException


def property_value(name, value):
    result = PropertyValue()
    result.Name = name
    result.Value = value
    return result


def text_documents(desktop):
    result = []
    entries = desktop.getComponents().createEnumeration()
    while entries.hasMoreElements():
        component = entries.nextElement()
        try:
            if component.supportsService("com.sun.star.text.TextDocument"):
                result.append(component)
        except DisposedException:
            # The blank document is normally replaced while .uno:Open loads the selected one.
            continue
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--office-dir", default=r"C:\Program Files\NAI_OFFICE 27\program")
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--debugger")
    parser.add_argument("--files-debugger")
    parser.add_argument("--allow-desktop-ui", action="store_true")
    args = parser.parse_args()
    if not args.allow_desktop_ui:
        parser.error("This tracer opens desktop windows. Use an isolated desktop or explicit approval with --allow-desktop-ui.")
    office = Path(args.office_dir)
    clsid = r"Software\Classes\CLSID\{DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7}\InprocServer32"
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, clsid) as key:
        bridge = winreg.QueryValueEx(key, "")[0]
    if f"_{args.expected_version}_" not in bridge:
        raise RuntimeError(f"Wrong registered bridge for test: {bridge}")

    proof_root = Path(tempfile.mkdtemp(prefix="nai-writer-com-"))
    print(f"TRACE_ROOT {proof_root}", flush=True)
    profile = proof_root / "profile"
    pipe = "nai_writer_com_" + uuid.uuid4().hex
    environment = os.environ.copy()
    environment["NAI_FILES_DIALOG_TRACE"] = "writer"
    # A separate profile prevents redirection into, or termination of, the user's Office instance.
    command = [
        str(office / "soffice.exe"),
        f"-env:UserInstallation={profile.as_uri()}",
        f"--accept=pipe,name={pipe};urp;StarOffice.ComponentContext",
        "--nologo", "--writer", "--norestore", "--nofirststartwizard",
    ]
    if args.debugger:
        crash_command = ".echo WRITER_FAIL_FAST; .exr -1; .ecxr; kp 50; q"
        command = [args.debugger, "-o", "-g", "-G", "-logo", str(proof_root / "debugger.log"), "-c", crash_command] + command
    desktop = None
    debugging = []
    stop_debugging = threading.Event()
    with (proof_root / "office.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, env=environment, stdout=log, stderr=log)
        try:
            local = uno.getComponentContext()
            resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
            deadline = time.monotonic() + 60
            while True:
                try:
                    context = resolver.resolve(f"uno:pipe,name={pipe};urp;StarOffice.ComponentContext")
                    break
                except Exception:
                    if time.monotonic() > deadline:
                        raise RuntimeError(f"Isolated Writer did not expose its UNO pipe; log: {proof_root}")
                    time.sleep(0.2)

            services = context.ServiceManager
            configuration = services.createInstanceWithContext("com.sun.star.configuration.ConfigurationProvider", context)
            settings = configuration.createInstanceWithArguments(
                "com.sun.star.configuration.ConfigurationUpdateAccess",
                (property_value("nodepath", "/org.openoffice.Office.Common/Misc"),),
            )
            settings.setPropertyValue("UseSystemFileDialog", True)
            settings.commitChanges()

            desktop = services.createInstanceWithContext("com.sun.star.frame.Desktop", context)
            deadline = time.monotonic() + 60
            blank = None
            while time.monotonic() < deadline:
                documents = text_documents(desktop)
                if documents:
                    blank = documents[0]
                    break
                time.sleep(0.2)
            if blank is None:
                raise RuntimeError("Writer startup did not create its normal document frame.")
            command_url = uno.createUnoStruct("com.sun.star.util.URL")
            command_url.Complete = ".uno:Open"
            transformer = services.createInstanceWithContext("com.sun.star.util.URLTransformer", context)
            valid, command_url = transformer.parseStrict(command_url)
            if not valid:
                raise RuntimeError("Writer Open command URL could not be parsed.")
            dispatch = blank.getCurrentController().getFrame().queryDispatch(command_url, "", 0)
            if args.files_debugger:
                temporary = Path(tempfile.gettempdir())
                existing = set(temporary.glob("nai-dialog-*.txt.pid"))

                def attach_files_debugger():
                    deadline = time.monotonic() + 60
                    while time.monotonic() < deadline and not stop_debugging.is_set():
                        for marker in set(temporary.glob("nai-dialog-*.txt.pid")) - existing:
                            try:
                                pid = int(marker.read_text(encoding="utf-8"))
                            except (OSError, ValueError):
                                continue
                            command = [args.files_debugger, "-p", str(pid), "-G", "-y", str(Path(__file__).resolve().parents[2] / "artifacts"), "-logo", str(proof_root / "files-debugger.log"),
                                       "-c", 'sxd -c2 ".echo FILES_CRASH; .exr -1; .ecxr; kp 50; q" av; g']
                            debugging.append(subprocess.Popen(command, stdout=log, stderr=log))
                            return
                        time.sleep(0.02)

                threading.Thread(target=attach_files_debugger, daemon=True).start()
            # Menu Open is asynchronous. DispatchHelper forces a synchronous result listener,
            # which is not the menu's threading path. No FileName/URL argument bypasses the picker.
            dispatch.dispatch(command_url, (property_value("SynchronMode", False),))

            deadline = time.monotonic() + 180
            opened = None
            while time.monotonic() < deadline:
                for document in text_documents(desktop):
                    try:
                        url = document.getURL()
                    except DisposedException:
                        continue
                    if "nai-dialog-trace-" in url and url.endswith("Writer.fodt"):
                        opened = document
                        break
                if opened is not None:
                    break
                time.sleep(0.2)
            if opened is None:
                urls = [document.getURL() for document in text_documents(desktop)]
                raise RuntimeError(f"Writer did not load the picker result. Documents: {urls}; log: {proof_root}")

            content = opened.getText().getString()
            if content.strip() != "NAI Writer COM acceptance tracer":
                raise RuntimeError(f"Writer loaded unexpected document contents: {content!r}")
            selected = Path(uno.fileUrlToSystemPath(opened.getURL()))
            token = selected.parent.parent.name.removeprefix("nai-dialog-trace-")
            response = Path(tempfile.gettempdir()) / f"nai-dialog-{token}.txt"
            native_trace = Path(str(response) + ".native.trace").read_text(encoding="utf-8")
            managed_trace = Path(str(response) + ".trace").read_text(encoding="utf-8")
            if "OnFileOk=0x80004001" not in native_trace or "Show=0x0" not in native_trace:
                raise RuntimeError(f"Unexpected native callback/modal result: {native_trace}")
            if "FilesExit=0x0" not in native_trace:
                raise RuntimeError(f"Files did not exit cleanly after returning the document: {native_trace}")
            if "FAIL " in managed_trace:
                raise RuntimeError(f"Files backend trace failed: {managed_trace}")
            proof = {
                "version": args.expected_version,
                "office": str(office / "soffice.exe"),
                "bridge": bridge,
                "command": ".uno:Open (no filename argument)",
                "dispatchMode": "asynchronous XDispatch (menu-equivalent)",
                "documentUrl": opened.getURL(),
                "documentText": content,
                "nativeTrace": native_trace.splitlines(),
                "filesTrace": str(response) + ".trace",
                "profile": str(profile),
            }
            (proof_root / "result.json").write_text(json.dumps(proof, indent=2, ensure_ascii=False), encoding="utf-8")
            print("PASS real Writer .uno:Open loaded the exact Files-selected document")
            print(json.dumps(proof, indent=2, ensure_ascii=False))
            print(f"PROOF {proof_root / 'result.json'}")
        finally:
            stop_debugging.set()
            if desktop is not None:
                try:
                    for document in text_documents(desktop):
                        document.close(True)
                except Exception:
                    pass
                try:
                    desktop.terminate()
                except Exception:
                    pass
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=10)
            for debugger in debugging:
                try:
                    debugger.wait(timeout=120)
                except subprocess.TimeoutExpired:
                    debugger.terminate()


if __name__ == "__main__":
    main()
