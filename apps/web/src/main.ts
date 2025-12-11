import * as monaco from "monaco-editor";
import loadGhostscript from "@okathira/ghostpdl-wasm";

// Type definitions
interface GhostscriptModule {
  FS: {
    writeFile(path: string, data: Uint8Array): void;
    readFile(path: string, options: { encoding: "binary" }): Uint8Array;
    mkdir(path: string): void;
    chdir(path: string): void;
  };
  callMain(args: string[]): number;
}

// Initialize Monaco Editor
let editor: monaco.editor.IStandaloneCodeEditor;
let ghostscriptModule: GhostscriptModule | null = null;

const defaultCode = `// Write your C# code here
// Embedded Functions are supported:
//   void println(string s);
//   void print(string s);
//   void WriteLine(string s);
//   void Write(string s);
// Change FontFace with:
//   [assembly:PSFont("Courier", 12)]

int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

var ch = 'H';
println($"{ch}ello {(char)(Fib(11) - 2)}orld!");
`;

function initEditor(): void {
  const editorElement = document.getElementById("editor");
  if (!editorElement) {
    throw new Error("Editor element not found");
  }

  const savedCode = localStorage.getItem("cspsc-code");
  const initialCode = savedCode ?? defaultCode;

  editor = monaco.editor.create(editorElement, {
    value: initialCode,
    language: "csharp",
    theme: "vs-dark",
    automaticLayout: true,
    fontSize: 14,
    minimap: { enabled: true },
    scrollBeyondLastLine: false,
    wordWrap: "on",
  });

  // Save code to localStorage on change
  editor.onDidChangeModelContent(() => {
    const code = editor.getValue();
    localStorage.setItem("cspsc-code", code);
  });
}

// Initialize Ghostscript WASM
async function initGhostscript(): Promise<void> {
  if (!ghostscriptModule) {
    console.log("Loading Ghostscript WASM...");
    ghostscriptModule = await loadGhostscript();
    console.log("Ghostscript WASM loaded successfully");
  }
}

// Tab switching functionality
function setupTabs(): void {
  const tabButtons = document.querySelectorAll<HTMLButtonElement>(".tab-btn");
  const tabPanels = document.querySelectorAll<HTMLDivElement>(".tab-panel");

  tabButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const targetTab = button.getAttribute("data-tab");
      if (!targetTab) return;

      // Remove active class from all buttons and panels
      tabButtons.forEach((btn) => btn.classList.remove("active"));
      tabPanels.forEach((panel) => panel.classList.remove("active"));

      // Add active class to clicked button and corresponding panel
      button.classList.add("active");
      const targetPanel = document.getElementById(`${targetTab}-tab`);
      if (targetPanel) {
        targetPanel.classList.add("active");
      }
    });
  });
}

// Convert PostScript to PDF using Ghostscript WASM
async function convertPostScriptToPDF(postscriptData: string): Promise<Blob> {
  if (!ghostscriptModule) {
    await initGhostscript();
  }

  if (!ghostscriptModule) {
    throw new Error("Failed to load Ghostscript module");
  }

  const inputFilename = "input.ps";
  const outputFilename = "output.pdf";

  // Write PostScript data to virtual filesystem
  const encoder = new TextEncoder();
  const psBytes = encoder.encode(postscriptData);
  ghostscriptModule.FS.writeFile(inputFilename, psBytes);

  // Convert PostScript to PDF
  const exitCode = ghostscriptModule.callMain([
    "-sDEVICE=pdfwrite",
    "-dNOPAUSE",
    "-dBATCH",
    "-dSAFER",
    "-sOutputFile=" + outputFilename,
    inputFilename,
  ]);

  if (exitCode !== 0) {
    throw new Error(`Ghostscript conversion failed with exit code ${exitCode}`);
  }

  // Read the output PDF file
  const pdfBytes = ghostscriptModule.FS.readFile(outputFilename, {
    encoding: "binary",
  });

  // Create a Blob from the PDF bytes
  return new Blob([new Uint8Array(pdfBytes)], { type: "application/pdf" });
}

// Display PDF in embed tag
function displayPDF(pdfBlob: Blob): void {
  const pdfViewer = document.getElementById(
    "pdf-viewer"
  ) as HTMLEmbedElement | null;
  if (!pdfViewer) {
    console.error("PDF viewer element not found");
    return;
  }

  // Create object URL and set it to embed element
  const url = URL.createObjectURL(pdfBlob);
  pdfViewer.src = url;
}

// Compile functionality
async function compile(): Promise<void> {
  const output = document.getElementById("output");
  if (!output) return;

  output.textContent = "% Compiling...";

  try {
    const code = editor.getValue();
    const response = await fetch("/compile", {
      method: "POST",
      headers: { "Content-Type": "text/plain" },
      body: code,
    });

    const result = await response.text();
    output.textContent = result;

    // Check if the result is PostScript
    output.textContent += "\n\n% Converting PostScript to PDF...";

    try {
      const pdfBlob = await convertPostScriptToPDF(result);
      displayPDF(pdfBlob);
      output.textContent = result + "\n\n% PDF generated successfully";
    } catch (error) {
      const errorMessage =
        error instanceof Error ? error.message : "Unknown error";
      output.textContent += `\n\n% PDF conversion error: ${errorMessage}`;
      console.error("PDF conversion error:", error);
    }
  } catch (error) {
    const errorMessage =
      error instanceof Error ? error.message : "Unknown error";
    output.textContent = `% Error: ${errorMessage}`;
  }
}

function setupCompileButton(): void {
  const compileBtn = document.getElementById("compileBtn");
  if (compileBtn) {
    compileBtn.addEventListener("click", compile);
  }
}

// Resizer functionality
function setupResizer(): void {
  const resizer = document.querySelector(".resizer") as HTMLElement | null;
  const leftPane = document.querySelector(".left-pane") as HTMLElement | null;
  const rightPane = document.querySelector(".right-pane") as HTMLElement | null;
  const container = document.querySelector(".container") as HTMLElement | null;

  if (!resizer || !leftPane || !rightPane || !container) {
    return;
  }

  let isResizing = false;

  resizer.addEventListener("pointerdown", (e: PointerEvent) => {
    isResizing = true;
    resizer.classList.add("resizing");
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";
    resizer.setPointerCapture(e.pointerId);
    e.preventDefault();
  });

  document.addEventListener("pointermove", (e: PointerEvent) => {
    if (!isResizing) return;

    const containerRect = container.getBoundingClientRect();
    const leftWidth = e.clientX - containerRect.left;
    const totalWidth = containerRect.width;
    const leftPercentage = (leftWidth / totalWidth) * 100;

    // Limit resizing to 20%-80%
    if (leftPercentage >= 20 && leftPercentage <= 80) {
      leftPane.style.flex = `0 0 ${leftPercentage}%`;
      rightPane.style.flex = `0 0 ${100 - leftPercentage}%`;
    }
  });

  document.addEventListener("pointerup", (e: PointerEvent) => {
    if (isResizing) {
      isResizing = false;
      resizer.classList.remove("resizing");
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
      resizer.releasePointerCapture(e.pointerId);
    }
  });
}

// Initialize on page load
window.addEventListener("DOMContentLoaded", async () => {
  initEditor();
  setupTabs();
  setupCompileButton();
  setupResizer();

  // Preload Ghostscript WASM in the background
  initGhostscript().catch((error) => {
    console.error("Failed to preload Ghostscript:", error);
  });
});

// Export for potential use in other modules
export { compile, convertPostScriptToPDF };
