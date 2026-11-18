define([], function () {
    "use strict";

    // 必须与 CommonConfiguration.PluginId 保持一致
    var pluginId = "a1b2c3d4-e5f6-7890-abcd-ef1234567891";
    var defaultProcessingDelayMs = 2000;
    var defaultMaxConcurrency = 3;
    var submitting = false;

    function clamp(value, min, max, fallbackValue) {
        if (isNaN(value)) {
            return fallbackValue;
        }

        return Math.max(min, Math.min(max, value));
    }

    function loadConfig(view) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector("#EnableAutoExtract").checked = !!config.EnableAutoExtract;
            view.querySelector("#ProcessingDelayMs").value =
                config.ProcessingDelayMs != null ? Number(config.ProcessingDelayMs) : defaultProcessingDelayMs;
            view.querySelector("#MaxConcurrency").value =
                config.MaxConcurrency != null ? Number(config.MaxConcurrency) : defaultMaxConcurrency;
            Dashboard.hideLoadingMsg();
        }, function () {
            Dashboard.hideLoadingMsg();
        });
    }

    function onSubmit(e) {
        var form = this;
        if (form.reportValidity && !form.reportValidity()) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }

        e.preventDefault();
        e.stopPropagation();

        if (submitting) {
            return false;
        }

        var processingDelayMs = parseInt(form.querySelector("#ProcessingDelayMs").value, 10);
        var maxConcurrency = parseInt(form.querySelector("#MaxConcurrency").value, 10);

        var config = {
            EnableAutoExtract: form.querySelector("#EnableAutoExtract").checked,
            ProcessingDelayMs: clamp(processingDelayMs, 0, 20000, defaultProcessingDelayMs),
            MaxConcurrency: clamp(maxConcurrency, 1, 10, defaultMaxConcurrency)
        };

        submitting = true;
        Dashboard.showLoadingMsg();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
            submitting = false;
            Dashboard.processPluginConfigurationUpdateResult(result);
        }, function () {
            submitting = false;
            Dashboard.hideLoadingMsg();
        });

        return false;
    }

    return function (view) {
        var form = view.querySelector("form");
        var submitButton = view.querySelector(".button-submit");
        form.addEventListener("submit", onSubmit);
        if (submitButton) {
            submitButton.addEventListener("click", function (e) {
                onSubmit.call(form, e);
            });
        }

        // Load once on init to cover first render, and on every revisit.
        loadConfig(view);
        view.addEventListener("viewshow", function () {
            loadConfig(view);
        });
    };
});
