// Fails fast when the dev password is missing. It names the variable, never a value.
export default function globalSetup(): void {
  const password = process.env.E2E_DEV_PASSWORD;
  if (password === undefined || password.trim() === '') {
    throw new Error(
      'E2E_DEV_PASSWORD is not set. Set it in the current shell only ' +
        "(for example: $env:E2E_DEV_PASSWORD = Read-Host -MaskInput 'dev password') and run again.",
    );
  }
}
