// Lets tests import a source file as text (Vite's ?raw suffix).
declare module '*?raw' {
  const content: string;
  export default content;
}
