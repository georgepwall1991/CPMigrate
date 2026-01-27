# Contributing to CPMigrate

First off, thanks for taking the time to contribute! 🎉

The following is a set of guidelines for contributing to CPMigrate. These are mostly guidelines, not rules. Use your best judgment, and feel free to propose changes to this document in a pull request.

## 🛠️ How to Contribute

### Reporting Bugs

This section guides you through submitting a bug report for CPMigrate. Following these guidelines helps maintainers and the community understand your report, reproduce the behavior, and find related reports.

-   **Use a clear and descriptive title** for the issue to identify the problem.
-   **Describe the exact steps which reproduce the problem** in as many details as possible.
-   **Provide specific examples** to demonstrate the steps. Include links to files or GitHub projects, or copy/pasteable snippets, which you use in those examples.

### Suggesting Enhancements

This section guides you through submitting an enhancement suggestion for CPMigrate, including completely new features and minor improvements to existing functionality.

-   **Use a clear and descriptive title** for the issue to identify the suggestion.
-   **Provide a step-by-step description of the suggested enhancement** in as many details as possible.
-   **Explain why this enhancement would be useful** to most CPMigrate users.

## 💻 Development Process

1.  **Fork the repo** and create your branch from `main`.
2.  **Install dependencies**:
    ```bash
    dotnet restore
    ```
3.  **Run tests**:
    ```bash
    dotnet test
    ```
4.  **Make your changes**.
5.  **Ensure CI passes**.

## 🎨 Style Guide

-   Use **file-scoped namespaces**.
-   Follow the existing **Spectre.Console** patterns for UI.
-   Ensure all new code is covered by **unit tests**.

## 📝 License

By contributing, you agree that your contributions will be licensed under its MIT License.
