// Run this in your browser's developer console while logged in to the admin area
// Or add to a Razor page temporarily

// Copy-paste this JSON into the Node Designer's Import feature:
const nodeDefinition = {
    "exportVersion": "1.0",
    "exportedAt": "2026-01-15T17:40:00Z",
    "definition": {
        "nodeTypeKey": "Custom_JsonToXml",
        "displayName": "JSON to XML",
        "description": "Converts JSON data to XML format. Supports nested objects, arrays, and primitive values.",
        "categoryName": "Data Transformation",
        "iconSvg": "<svg viewBox=\"0 0 24 24\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M13 3H8.2C7.0799 3 6.51984 3 6.09202 3.21799C5.71569 3.40973 5.40973 3.71569 5.21799 4.09202C5 4.51984 5 5.0799 5 6.2V17.8C5 18.9201 5 19.4802 5.21799 19.908C5.40973 20.2843 5.71569 20.5903 6.09202 20.782C6.51984 21 7.0799 21 8.2 21H15.8C16.9201 21 17.4802 21 17.908 20.782C18.2843 20.5903 18.5903 20.2843 18.782 19.908C19 19.4802 19 18.9201 19 17.8V9M13 3L19 9M13 3V7.4C13 7.96005 13 8.24008 13.109 8.45399C13.2049 8.64215 13.3578 8.79513 13.546 8.89101C13.7599 9 14.0399 9 14.6 9H19\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/><path d=\"M9 15L11 12L9 9\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/><path d=\"M15 15L13 12L15 9\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>",
        "executionType": "DataTransformation",
        "timeoutSeconds": 30,
        "script": `// JSON to XML Converter
var jsonInput = input.get("jsonInput");
var rootElement = (input.get("rootElement") || "root").trim();
var indent = input.get("indent") === "true";
var includeDeclaration = input.get("includeDeclaration") !== "false";
var arrayItemName = (input.get("arrayItemName") || "item").trim();

if (!jsonInput || jsonInput.trim() === "") {
    log.error("JSON input is required");
    output.set("HasErrors", true);
    output.set("ErrorMessage", "JSON input is required");
    output.set("Xml", "");
    tags.trigger("error");
} else {
    try {
        var data = json.parse(jsonInput);
        
        function escapeXml(str) {
            if (str === null || str === undefined) return "";
            var s = String(str);
            return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&apos;");
        }
        
        function sanitizeTagName(name) {
            if (!name) return "element";
            var n = String(name).replace(/[^a-zA-Z0-9_.-]/g, "_");
            if (!/^[a-zA-Z_]/.test(n)) n = "_" + n;
            return n;
        }
        
        function toXml(value, tagName, depth) {
            var indentStr = indent ? new Array(depth + 1).join("  ") : "";
            var newline = indent ? "\\n" : "";
            var tag = sanitizeTagName(tagName);
            
            if (value === null || value === undefined) return indentStr + "<" + tag + " />" + newline;
            if (typeof value === "boolean" || typeof value === "number") return indentStr + "<" + tag + ">" + String(value) + "</" + tag + ">" + newline;
            if (typeof value === "string") return indentStr + "<" + tag + ">" + escapeXml(value) + "</" + tag + ">" + newline;
            
            if (Array.isArray(value)) {
                var arrXml = "";
                for (var i = 0; i < value.length; i++) arrXml = arrXml + toXml(value[i], arrayItemName, depth);
                return arrXml;
            }
            
            if (typeof value === "object") {
                var objXml = indentStr + "<" + tag + ">" + newline;
                var keys = Object.keys(value);
                for (var j = 0; j < keys.length; j++) {
                    var k = keys[j], v = value[k];
                    if (Array.isArray(v)) {
                        objXml = objXml + indentStr + (indent ? "  " : "") + "<" + sanitizeTagName(k) + ">" + newline;
                        for (var m = 0; m < v.length; m++) objXml = objXml + toXml(v[m], arrayItemName, depth + 2);
                        objXml = objXml + indentStr + (indent ? "  " : "") + "</" + sanitizeTagName(k) + ">" + newline;
                    } else {
                        objXml = objXml + toXml(v, k, depth + 1);
                    }
                }
                return objXml + indentStr + "</" + tag + ">" + newline;
            }
            
            return indentStr + "<" + tag + ">" + escapeXml(String(value)) + "</" + tag + ">" + newline;
        }
        
        var xml = includeDeclaration ? "<?xml version=\\"1.0\\" encoding=\\"UTF-8\\"?>" + (indent ? "\\n" : "") : "";
        
        if (Array.isArray(data)) {
            xml = xml + "<" + rootElement + ">" + (indent ? "\\n" : "");
            for (var x = 0; x < data.length; x++) xml = xml + toXml(data[x], arrayItemName, 1);
            xml = xml + "</" + rootElement + ">";
        } else {
            xml = xml + toXml(data, rootElement, 0);
        }
        
        output.set("Xml", xml);
        output.set("HasErrors", false);
        output.set("ErrorMessage", "");
        log.info("Successfully converted JSON to XML (" + xml.length + " chars)");
        tags.trigger("success");
    } catch (ex) {
        log.error("Failed to convert JSON: " + ex.message);
        output.set("HasErrors", true);
        output.set("ErrorMessage", "JSON parse error: " + ex.message);
        output.set("Xml", "");
        tags.trigger("error");
    }
}`,
        "inputFields": [
            { "fieldName": "jsonInput", "displayLabel": "JSON Input", "fieldType": "TextArea", "isRequired": true, "allowPlaceholders": true, "displayOrder": 1 },
            { "fieldName": "rootElement", "displayLabel": "Root Element Name", "fieldType": "Text", "defaultValue": "root", "displayOrder": 2 },
            { "fieldName": "arrayItemName", "displayLabel": "Array Item Name", "fieldType": "Text", "defaultValue": "item", "displayOrder": 3 },
            { "fieldName": "indent", "displayLabel": "Pretty Print", "fieldType": "Boolean", "defaultValue": "true", "displayOrder": 4 },
            { "fieldName": "includeDeclaration", "displayLabel": "Include XML Declaration", "fieldType": "Boolean", "defaultValue": "true", "displayOrder": 5 }
        ],
        "outputParameters": [
            { "parameterName": "Xml", "description": "The converted XML string", "displayOrder": 1 },
            { "parameterName": "HasErrors", "description": "True if error occurred", "displayOrder": 2 },
            { "parameterName": "ErrorMessage", "description": "Error details", "displayOrder": 3 }
        ],
        "connectionTags": [
            { "tagName": "success", "description": "Conversion successful", "color": "#22c55e", "displayOrder": 1 },
            { "tagName": "error", "description": "Conversion failed", "color": "#ef4444", "displayOrder": 2 }
        ]
    }
};

console.log("Copy the JSON below and paste into the Node Designer Import dialog:");
console.log(JSON.stringify(nodeDefinition, null, 2));
